#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.BookingFulfillment.Cancellation;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake provider adapter ────────────────────────────────────────────────────────────────────

/// <summary>
/// Test-controllable provider adapter for T8 compensation and cancellation-race tests.
/// Supports:
///   - Configurable CreateBookingAsync result (Created, PermanentError, …).
///   - Configurable CancelBookingAsync result (Created=acknowledged, Ambiguous, …).
///   - CancelCallCount to assert CancelBookingAsync was (or was not) invoked.
/// </summary>
file sealed class T8FakeBookingProviderAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake";

    // Create result — default Created.
    public ProviderResultKind CreateResult { get; set; } = ProviderResultKind.Created;
    public string CreateRef { get; set; } = "fake_ref_t8";

    // Cancel result — default Created (cancel acknowledged).
    public ProviderResultKind CancelResult { get; set; } = ProviderResultKind.Created;

    // Call counters.
    public int CreateCallCount { get; private set; }
    public int CancelCallCount { get; private set; }

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
    {
        CreateCallCount++;
        return Task.FromResult(CreateResult switch
        {
            ProviderResultKind.Created => new ProviderResult(ProviderResultKind.Created, CreateRef),
            _ => new ProviderResult(CreateResult),
        });
    }

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(
        string providerAccount, string providerIdempotencyKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> LookupByReferenceAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> CancelBookingAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
    {
        CancelCallCount++;
        return Task.FromResult(new ProviderResult(CancelResult));
    }
}

// ── Test class ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for T8: compensation (CompensationRequired → FailedFinal / ManualReview)
/// and the in-flight cancellation races (FM#13, spec 06 §9, §11, §15.5).
///
/// Scenarios:
///   1. Permanent finalize error → CompensationRequired → FailedFinal (no booking created).
///   2. Cancel-after-create → CompensationRequired → cancel booking → FailedFinal.
///   3. Cancel-before-create → CompensationRequired → FailedFinal (nothing to cancel).
///   4. Compensation ambiguous → ManualReview + work item + still_confirming outbox.
///   5. RequestCancellationCommand idempotency: first call sets flag, second call returns false.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CompensationAndCancellationTests(PostgresFixture fixture)
{
    // ── Container builder ─────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer(IBookingProviderAdapter fakeAdapter)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);

        // Override the provider adapter (last AddSingleton wins).
        services.AddSingleton<IBookingProviderAdapter>(fakeAdapter);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    private async Task NeutralizeExistingUnpublishedAsync()
    {
        await using var ctx = fixture.CreateContext("t8-cleanup");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    // ── Scenario 1: Permanent finalize error → CompensationRequired → FailedFinal ──────────

    /// <summary>
    /// FM#12 / §9: adapter returns PermanentError on CreateBooking.
    /// Expected path: Accepted → … → Finalizing (create call → PermanentError) →
    ///   CompensationRequired → Compensate (providerRef null) → FailedFinal.
    /// CancelBooking MUST NOT be called (no booking was created).
    /// </summary>
    [Fact]
    public async Task PermanentFinalizeError_CompensationRequired_ReachesFailedFinal()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-perm-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Accepted);

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.PermanentError,
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: CancelBooking NOT called (no booking was created) ─────────────────────
        Assert.Equal(0, fakeAdapter.CancelCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → FailedFinal ──────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        // ── Assert: compensate workflow step → Succeeded ──────────────────────────────────
        var compensateStep = await assertCtx.WorkflowSteps
            .AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.TenantId == tenantId &&
                s.OperationId == operationId &&
                s.StepType == "compensate");

        Assert.NotNull(compensateStep);
        Assert.Equal(WorkflowStepStatus.Succeeded, compensateStep!.Status);

        // ── Assert: notification.booking_failed.v1 outbox row exists ─────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.failed_final or operation.compensated audit event exists ────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                (e.EventType == "operation.failed_final" || e.EventType == "operation.compensated"));

        Assert.NotNull(audit);
    }

    // ── Scenario 2: Cancel-after-create → compensate the booking → FailedFinal ────────────

    /// <summary>
    /// FM#13 (cancel-after-create): seeds op already in CompensationRequired (reason
    /// "cancelled_after_create") with a finalize attempt that has a ProviderRef — representing
    /// the state AFTER FinalizeBookingCommandHandler detected cancellation-after-create and
    /// routed to CompensationRequired.
    ///
    /// The driver dispatches CompensateOperationCommand → adapter.CancelBookingAsync called
    /// (CancelCallCount == 1) → op → FailedFinal + booking_failed notification.
    /// booking_confirmed must NOT exist.
    /// </summary>
    [Fact]
    public async Task CancelAfterCreate_CompensationRequired_CancelsCbooking_ReachesFailedFinal()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-cac-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        const string providerRef = "fake_ref_t8_cac";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op already in CompensationRequired — as left by Finalize detecting cancel-after-create.
            operationId = await TestData.SeedOperationAsync(
                seedCtx, tenantId, OperationStatus.CompensationRequired);

            // Set status reason to reflect cancelled_after_create.
            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(o => o.StatusReason, "cancelled_after_create")
                    .SetProperty(o => o.CancellationRequestedAt, DateTimeOffset.UtcNow));

            var providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // Seed a finalize attempt with a ProviderRef (the booking WAS created by Finalize).
            seedCtx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att_{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = "finalize",
                ProviderIdempotencyKey = providerKey,
                ProviderName = "fake",
                ProviderAccount = "default",
                RequestHash = "h",
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Succeeded,
                ProviderRef = providerRef,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });

            // Outbox trigger.
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CancelResult = ProviderResultKind.Created, // cancel acknowledged
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: CancelBooking WAS called exactly once ─────────────────────────────────
        Assert.Equal(1, fakeAdapter.CancelCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → FailedFinal ──────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        // ── Assert: notification.booking_failed.v1 outbox row exists ─────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");

        Assert.NotNull(notification);

        // ── Assert: booking_confirmed was NOT emitted ─────────────────────────────────────
        var confirmed = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");

        Assert.Null(confirmed);
    }

    // ── Scenario 2b: Finalize DETECTS cancel-after-create end-to-end (the headline FM#13 path) ──

    /// <summary>
    /// FM#13 headline: exercises the Finalize re-read-and-route branch
    /// (FinalizeBookingCommandHandler success TXN2). Seeds an op in Finalizing WITH
    /// CancellationRequestedAt set, a finalize step InProgress, and a prior TransientFailure
    /// attempt (so neither the idempotent shortcut, the Inv-7 guard, nor the cancel-before-create
    /// guard fire — the handler proceeds to TXN1 and calls CreateBooking).
    ///
    /// Path: Finalize creates booking (Created) → success TXN2 re-reads op → detects cancellation
    ///   → Finalizing → CompensationRequired (reason "cancelled_after_create") + audit
    ///   operation.cancellation_after_create → driver dispatches Compensate → CancelBooking called
    ///   (CancelCallCount == 1) → FailedFinal.
    ///
    /// Asserts: op → FailedFinal; CancelCallCount == 1; NO booking_confirmed; the
    /// operation.cancellation_after_create audit event exists; booking_failed notification exists.
    /// </summary>
    [Fact]
    public async Task FinalizeDetectsCancelAfterCreate_CompensatesBooking_ReachesFailedFinal()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-detect-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        const string createdRef = "fake_ref_t8_detect";
        string operationId;
        string providerKey;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op in Finalizing WITH CancellationRequestedAt set.
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Finalizing);

            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(o => o.CancellationRequestedAt, DateTimeOffset.UtcNow));

            providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // Seed the finalize WorkflowStep (InProgress).
            seedCtx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                OperationId = operationId,
                StepType = "finalize",
                Status = WorkflowStepStatus.InProgress,
                ProviderIdempotencyKey = providerKey,
                AttemptCount = 1,
                Version = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            // Seed a prior TransientFailure attempt so all early guards fall through to TXN1:
            //   - NOT Succeeded/Reconciled (idempotent shortcut skipped)
            //   - NOT Dispatching/Ambiguous (Inv-7 guard skipped)
            //   - NOT null / Planned (cancel-before-create guard skipped)
            seedCtx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att_{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = "finalize",
                ProviderIdempotencyKey = providerKey,
                ProviderName = "fake",
                ProviderAccount = "default",
                RequestHash = "h",
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.TransientFailure,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });

            // Outbox trigger.
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
            CreateRef = createdRef,
            CancelResult = ProviderResultKind.Created, // cancel acknowledged
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: booking WAS created, then cancelled exactly once ──────────────────────
        Assert.Equal(1, fakeAdapter.CreateCallCount);
        Assert.Equal(1, fakeAdapter.CancelCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → FailedFinal ──────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        // ── Assert: NO booking_confirmed outbox row ───────────────────────────────────────
        var confirmed = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");

        Assert.Null(confirmed);

        // ── Assert: operation.cancellation_after_create audit event exists ────────────────
        var detectAudit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.cancellation_after_create");

        Assert.NotNull(detectAudit);

        // ── Assert: notification.booking_failed.v1 outbox row exists ─────────────────────
        var failed = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");

        Assert.NotNull(failed);
    }

    // ── Scenario 3: Cancel-before-create → CompensationRequired → FailedFinal (nothing to cancel) ──

    /// <summary>
    /// FM#13 (cancel-before-create): op seeded in Held with CancellationRequestedAt set,
    /// NO finalize attempt. Driver enters FinalizeBookingCommandHandler, detects cancellation
    /// before TXN1 → CompensationRequired → Compensate (providerRef null) → FailedFinal.
    /// CancelCallCount == 0.
    /// </summary>
    [Fact]
    public async Task CancelBeforeCreate_CompensationRequired_NothingToCancel_ReachesFailedFinal()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-cbc-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op in Held WITH CancellationRequestedAt set (cancel before any finalize dispatch).
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Held);

            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(o => o.CancellationRequestedAt, DateTimeOffset.UtcNow));

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
            CancelResult = ProviderResultKind.Created,
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: CancelBooking NOT called (no booking was ever created) ────────────────
        Assert.Equal(0, fakeAdapter.CancelCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → FailedFinal ──────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        // ── Assert: notification.booking_failed.v1 outbox row exists ─────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");

        Assert.NotNull(notification);
    }

    // ── Scenario 3b: FIX (C3) in-txn re-read guard — cancel-before-create from Finalizing state ──

    /// <summary>
    /// FIX (C3) regression guard: seeds op ALREADY in Finalizing with CancellationRequestedAt
    /// set and NO finalize attempt (latest == null).  The cancel-before-create code path must:
    ///   1. Re-read latest attempt INSIDE the transaction (the fix).
    ///   2. Find it still null (no concurrent dispatch changed it).
    ///   3. Proceed with Finalizing → CompensationRequired → FailedFinal.
    ///
    /// This proves the "still null" branch of the in-txn guard does not break the existing
    /// cancel-before-create behavior when the op enters Finalize already in Finalizing state
    /// (rather than from Held as in Scenario 3).
    /// </summary>
    [Fact]
    public async Task CancelBeforeCreate_AlreadyFinalizing_NoAttempt_StillReachesFailedFinal()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-cbc-fin-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op in Finalizing WITH CancellationRequestedAt set and NO finalize attempt.
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Finalizing);

            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(o => o.CancellationRequestedAt, DateTimeOffset.UtcNow));

            // Outbox trigger.
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
            CancelResult = ProviderResultKind.Created,
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: cancel-before-create fired → FailedFinal, no booking created ──────────
        Assert.Equal(0, fakeAdapter.CreateCallCount); // no booking was created

        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        // CancelBooking must NOT have been called (no booking was ever created).
        Assert.Equal(0, fakeAdapter.CancelCallCount);
    }

    // ── Scenario 4: Compensation ambiguous → ManualReview ────────────────────────────────────

    /// <summary>
    /// §11 CompensationRequired → ManualReview: op seeded in CompensationRequired with a finalize
    /// attempt that has a ProviderRef. Fake adapter CancelBookingAsync returns Ambiguous.
    /// Expected: op → ManualReview + operations_work_items row + notification.still_confirming.v1.
    /// </summary>
    [Fact]
    public async Task CompensationAmbiguous_EscalatesToManualReview()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t8-camb-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        const string providerRef = "fake_ref_t8_amb";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(
                seedCtx, tenantId, OperationStatus.CompensationRequired);

            var providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // Seed a finalize attempt with a ProviderRef (the booking WAS created).
            seedCtx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att_{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = "finalize",
                ProviderIdempotencyKey = providerKey,
                ProviderName = "fake",
                ProviderAccount = "default",
                RequestHash = "h",
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Succeeded,
                ProviderRef = providerRef,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });

            // Outbox trigger.
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });

            await seedCtx.SaveChangesAsync();
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter
        {
            CancelResult = ProviderResultKind.Ambiguous,
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → ManualReview ─────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.ManualReview, op!.Status);

        // ── Assert: operations_work_items row created ─────────────────────────────────────
        var workItem = await assertCtx.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w =>
                w.TenantId == tenantId &&
                w.OperationId == operationId);

        Assert.NotNull(workItem);
        Assert.Equal("open", workItem!.State);

        // ── Assert: notification.still_confirming.v1 outbox row exists ───────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.still_confirming.v1");

        Assert.NotNull(notification);
    }

    // ── Scenario 5: RequestCancellationCommand idempotency ───────────────────────────────────

    /// <summary>
    /// §15.5: RequestCancellationCommand sets the flag once. Second call returns false (already set).
    /// Terminal op (Booked) → not set.
    ///
    /// Direct mediator.Send requires the TenantContext to be set on the scoped DI container
    /// (normally the outbox publisher sets it via PostgresMessageTransport).
    /// </summary>
    [Fact]
    public async Task RequestCancellation_Idempotent_TerminalOpNotSet()
    {
        var tenantId = $"t8-reqc-{Guid.NewGuid():N}";

        string nonTerminalOpId;
        string terminalOpId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            nonTerminalOpId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Held);
            terminalOpId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);
            // SeedOperationAsync already calls SaveChangesAsync internally.
        }

        var fakeAdapter = new T8FakeBookingProviderAdapter();
        await using var sp = BuildContainer(fakeAdapter);

        // Must set TenantContext before direct mediator.Send calls so the global EF query filter
        // matches (GetAsync uses it without IgnoreQueryFilters).
        using var scope = sp.CreateScope();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // First call on non-terminal op → true (flag newly set).
        var firstResult = await mediator.Send(
            new RequestCancellationCommand(tenantId, nonTerminalOpId), CancellationToken.None);
        Assert.True(firstResult);

        // Second call → false (already set).
        var secondResult = await mediator.Send(
            new RequestCancellationCommand(tenantId, nonTerminalOpId), CancellationToken.None);
        Assert.False(secondResult);

        // Verify the flag is set in the DB.
        await using var assertCtx = fixture.CreateContext(tenantId);
        var opAfter = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == nonTerminalOpId);
        Assert.NotNull(opAfter!.CancellationRequestedAt);

        // Terminal op (Booked) → first call returns false (terminal status blocks the flag).
        var terminalResult = await mediator.Send(
            new RequestCancellationCommand(tenantId, terminalOpId), CancellationToken.None);
        Assert.False(terminalResult);

        var terminalOp = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == terminalOpId);
        Assert.Null(terminalOp!.CancellationRequestedAt);
    }
}
