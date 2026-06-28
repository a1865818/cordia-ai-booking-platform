#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake provider adapter ────────────────────────────────────────────────────────────────────

/// <summary>
/// Test-controllable booking provider adapter used by ReconciliationTests.
/// Supports:
///   - Pre-seeded "existing effect" for LookupByIdempotencyKey (simulates a crash-after-provider effect).
///   - Configurable Ambiguous behaviour on the Nth CreateBooking call.
///   - Counts CreateBooking calls to assert Inv-7 (no duplicate create during reconcile).
/// </summary>
file sealed class FakeBookingProviderAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake";

    // Pre-seed: if this key is set, LookupByIdempotencyKey returns AlreadyExists with SeedRef.
    public string? SeedKey { get; set; }
    public string? SeedRef { get; set; }

    // If > 0, the first N CreateBooking calls return Ambiguous; subsequent calls return Created.
    public int AmbiguousOnFirstNCreates { get; set; }

    // Tracks CreateBooking invocations.
    public int CreateCallCount { get; private set; }

    // Reference to return for successful creates (when not Ambiguous).
    public string CreateRef { get; set; } = "fake_ref_created";

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
    {
        CreateCallCount++;
        if (AmbiguousOnFirstNCreates > 0 && CreateCallCount <= AmbiguousOnFirstNCreates)
            return Task.FromResult(new ProviderResult(ProviderResultKind.Ambiguous));

        var reference = CreateRef;
        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, reference));
    }

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(
        string providerAccount, string providerIdempotencyKey, CancellationToken ct = default)
    {
        if (SeedKey is not null && providerIdempotencyKey == SeedKey && SeedRef is not null)
            return Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, SeedRef));

        return Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
    }

    public Task<ProviderResult> LookupByReferenceAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> CancelBookingAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
}

// ── Test class ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for the T5 reconciliation &amp; crash-after-provider recovery (spec 06 §16.2,
/// Inv-7, FM#2/#3).
///
/// FM#2 headline — crash-after-provider: op is in Finalizing + attempt is Dispatching + provider
/// already created the booking. Reconcile finds it via LookupByIdempotencyKey → op → Booked,
/// CreateBooking NEVER called (no duplicate).
///
/// FM#3 — no effect proven: all lookups return NotFound → Reconciling → Held → re-create under
/// same key → op → Booked (CreateBooking called exactly once after reconcile).
///
/// Ambiguous classification → Reconciling → resolve: the Nth create returns Ambiguous;
/// reconcile finds the effect via LookupByIdempotencyKey → Booked without a second create (Inv-7).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ReconciliationTests(PostgresFixture fixture)
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
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);

        // Override the StubBookingProviderAdapter with the test-controllable fake.
        // Last AddSingleton registration wins for GetRequiredService<IBookingProviderAdapter>.
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
        await using var ctx = fixture.CreateContext("t5-cleanup");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    // ── FM#2 headline: crash-after-provider — reconcile finds effect, no duplicate create ─────

    [Fact]
    public async Task FM2_CrashAfterProvider_ReconcileFindsEffect_NoDuplicateCreate()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t5-fm2-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        // The idempotency key the "crashed" worker would have used.
        string operationId;
        string providerKey;
        const string knownRef = "ext_crashed_ref";

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op in Finalizing (the state it's left in after TXN1 but before TXN2 commit).
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Finalizing);
            providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // Seed the finalize workflow_step (InProgress, with the providerKey).
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

            // Seed the Dispatching attempt (the "crashed worker" had marked Dispatching before calling the provider).
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
                Outcome = AttemptOutcome.Dispatching,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });

            // Outbox row to trigger the driver.
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

        // Configure fake adapter: LookupByIdempotencyKey returns AlreadyExists with knownRef.
        var fakeAdapter = new FakeBookingProviderAdapter
        {
            SeedKey = providerKey,
            SeedRef = knownRef,
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: CreateBooking was NEVER called (FM#2 — no duplicate) ───────────────────
        Assert.Equal(0, fakeAdapter.CreateCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → Booked with the recovered reference ─────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);
        Assert.Equal(knownRef, op.BookingId);

        // ── Assert: attempt → Reconciled with the recovered reference ────────────────────
        var attempt = await assertCtx.WorkflowAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.TenantId == tenantId &&
                a.OperationId == operationId &&
                a.WorkflowStep == "finalize");

        Assert.NotNull(attempt);
        Assert.Equal(AttemptOutcome.Reconciled, attempt!.Outcome);
        Assert.Equal(knownRef, attempt.ProviderRef);

        // ── Assert: notification.booking_confirmed.v1 outbox row exists (Inv-8) ──────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.reconciled audit event exists ───────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.reconciled");

        Assert.NotNull(audit);
    }

    // ── FM#3/FM#1: no effect proven → re-create under same key → Booked ──────────────────────

    [Fact]
    public async Task FM3_NoEffectProven_RecreatesUnderSameKey_ReachesBooked()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t5-fm3-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        string operationId;
        string providerKey;
        const string createdRef = "ext_recreated_ref";

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Finalizing);
            providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // Seed finalize step (InProgress) + Dispatching attempt.
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
                Outcome = AttemptOutcome.Dispatching,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });

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

        // Fake adapter: all lookups return NotFound; CreateBooking returns Created.
        // (SeedKey/SeedRef are null → LookupByIdempotencyKey returns NotFound).
        var fakeAdapter = new FakeBookingProviderAdapter { CreateRef = createdRef };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: op → Booked ────────────────────────────────────────────────────────────
        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);
        Assert.NotNull(op.BookingId);

        // ── Assert: CreateBooking was called exactly once (re-create after reconcile) ─────
        Assert.Equal(1, fakeAdapter.CreateCallCount);
    }

    // ── Ambiguous classification → Reconciling → resolve (Inv-7: reconcile before repeat) ───

    [Fact]
    public async Task AmbiguousClassification_ReconcileFindEffect_NeverCreatesSecondTime()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t5-amb-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed a fresh Accepted op (driver will advance it from scratch).
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

        // The idempotency key that will be derived during the finalize step.
        var providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);
        const string existingRef = "ext_ambiguous_ref";

        // Fake adapter:
        //   - First CreateBooking call returns Ambiguous.
        //   - LookupByIdempotencyKey returns AlreadyExists (the ambiguous create actually succeeded).
        var fakeAdapter = new FakeBookingProviderAdapter
        {
            AmbiguousOnFirstNCreates = 1,
            SeedKey = providerKey,
            SeedRef = existingRef,
            CreateRef = "ext_would_be_duplicate", // should never be returned
        };

        await using var sp = BuildContainer(fakeAdapter);
        var publisher = BuildPublisher(sp);

        // First tick: Accepted → … → Finalizing → Ambiguous → Reconciling (driver loop stops).
        // Because after the Ambiguous TXN2 the op is Reconciling, the driver loop continues
        // immediately within the same tick to dispatch ReconcileOperationCommand, which finds the
        // effect and transitions → Booked within the same tick.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: exactly ONE CreateBooking call (the ambiguous one — no duplicate) ────
        Assert.Equal(1, fakeAdapter.CreateCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → Booked with the reconciled reference ────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);
        Assert.Equal(existingRef, op.BookingId);

        // ── Assert: attempt has Reconciled outcome (not a second Succeeded attempt) ──────
        var attempt = await assertCtx.WorkflowAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.TenantId == tenantId &&
                a.OperationId == operationId &&
                a.WorkflowStep == "finalize" &&
                a.Outcome == AttemptOutcome.Reconciled);

        Assert.NotNull(attempt);
        Assert.Equal(existingRef, attempt!.ProviderRef);

        // ── Assert: notification outbox row exists ─────────────────────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.reconciled audit event exists ───────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.reconciled");

        Assert.NotNull(audit);
    }
}
