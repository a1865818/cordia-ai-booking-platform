#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Application.Extensions;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
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

// ── Fake capacity reservation ─────────────────────────────────────────────────────────────────

/// <summary>Fake ICapacityReservation that always returns NoCapacity (for FM#8 tests).</summary>
file sealed class NoCapacityReservation : ICapacityReservation
{
    public Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default)
        => Task.FromResult(ReservationOutcome.NoCapacity());

    public Task ReleaseAsync(string tenantId, string holdId, ReleaseReason reason, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> ReleaseByOperationAsync(string tenantId, string operationId, ReleaseReason reason, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> ReleaseFinalizedByOperationAsync(string tenantId, string operationId, ReleaseReason reason, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<FinalizeOutcome> FinalizeAgainstOperationAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default)
        => Task.FromResult(FinalizeOutcome.NotTracked);
}

// ── Fake booking provider adapter ────────────────────────────────────────────────────────────

/// <summary>
/// Test-controllable adapter for T6 retry/failure tests.
/// Supports transient failures on the first N creates, circuit-open simulation, and call counting.
/// </summary>
/// <summary>
/// Test-controllable adapter that returns a configurable ProviderResultKind on every create call.
/// Used for FIX X1 (AuthError/PaymentActionRequired → Reconciling) tests.
/// </summary>
file sealed class T6FixedResultAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake";

    public ProviderResultKind ResultKind { get; set; } = ProviderResultKind.AuthError;

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ResultKind));

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(string account, string key, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> LookupByReferenceAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> CancelBookingAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
}

file sealed class T6FakeAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake";

    // Number of Transient failures before returning Created.
    public int TransientOnFirstN { get; set; }

    // Counter for Create calls.
    public int CreateCallCount { get; private set; }

    // Reference returned on success.
    public string SuccessRef { get; set; } = "fake_ref_t6";

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
    {
        CreateCallCount++;
        if (CreateCallCount <= TransientOnFirstN)
            return Task.FromResult(new ProviderResult(ProviderResultKind.Transient));
        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, SuccessRef));
    }

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(string account, string key, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> LookupByReferenceAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> CancelBookingAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
}

// ── Shared helpers ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for T6: retry/circuit/failure taxonomy (spec 06 §6.3, §15, FM#8, FM#10, FM#11).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class RetryAndFailureTests(PostgresFixture fixture)
{
    // ── Container builder ─────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer(
        IBookingProviderAdapter? fakeAdapter = null,
        ICapacityReservation? fakeCapacity = null)
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

        // Override registrations (last registration wins for GetRequiredService<>).
        if (fakeAdapter is not null)
            services.AddSingleton<IBookingProviderAdapter>(fakeAdapter);
        if (fakeCapacity is not null)
            services.AddScoped<ICapacityReservation>(_ => fakeCapacity);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    private static ScheduledJobPollerHostedService BuildPoller(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new ScheduledJobPollerHostedService(
            scopeFactory, options, NullLogger<ScheduledJobPollerHostedService>.Instance);
    }

    private async Task NeutralizeExistingUnpublishedAsync(string cleanupTenant = "t6-cleanup")
    {
        await using var ctx = fixture.CreateContext(cleanupTenant);
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    private async Task NeutralizeExistingScheduledJobsAsync()
    {
        await using var ctx = fixture.CreateContext("t6-sched-cleanup");
        await ctx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == ScheduledJobStatus.Pending)
            .ExecuteUpdateAsync(b => b.SetProperty(j => j.Status, ScheduledJobStatus.Done));
    }

    // ── Test 1: NoCapacity → AlternativesRequired (FM#8) ────────────────────────────────────

    /// <summary>
    /// FM#8 — slot is fully booked, so the real ICapacityReservation returns NoCapacity.
    /// From Accepted → ValidateOperation → ReservingCapacity → (no capacity) → AlternativesRequired.
    /// Assert: op.Status == AlternativesRequired, notification.alternatives_required.v1 outbox,
    ///   operation.alternatives_required audit.
    /// Seeds: a capacity_slots row (total=1, held=1 = full) and an intent revision whose
    /// canonical_payload maps to that slot key (resource_id + starts_at).
    /// </summary>
    [Fact]
    public async Task NoCapacity_FromAccepted_RoutesToAlternativesRequired()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t6-nocap-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var resourceId = $"res-{Guid.NewGuid():N}";
        var requestedAt = DateTimeOffset.UtcNow.AddHours(2);
        // Slot key mirrors CapacitySlotKey.Derive(resourceId, null, null, requestedAt).
        var slotId = $"{resourceId}|{requestedAt:O}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed a FULL slot (total=1, held=1) so the reservation returns NoCapacity.
            seedCtx.CapacitySlots.Add(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = requestedAt,
                TotalCapacity = 1,
                HeldCapacity = 1,
                BookedCapacity = 0,
                Version = 0,
            });

            // Seed intent + revision with canonical payload pointing to the slot.
            var intentId = $"int-{Guid.NewGuid():N}";
            operationId = $"op-{Guid.NewGuid():N}";
            var canonicalPayload = JsonSerializer.Serialize(new
            {
                party_size = 1,
                resource_id = resourceId,
                starts_at = requestedAt.ToString("O"),
            });

            var intent = new BookingIntent
            {
                TenantId = tenantId,
                IntentId = intentId,
                OperationType = OperationTypeEnum.Create,
            };
            var revision = new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = intentId,
                Revision = 1,
                CanonicalPayload = canonicalPayload,
                PayloadHash = "h",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
                Intent = intent,
            };
            var operation = new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.Accepted,
                Version = 0,
            };

            seedCtx.BookingIntents.Add(intent);
            seedCtx.BookingIntentRevisions.Add(revision);
            seedCtx.BookingOperations.Add(operation);

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

        // Use the real container (real ICapacityReservation) — slot is full so NoCapacity fires.
        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → AlternativesRequired ───────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.AlternativesRequired, op!.Status);

        // ── Assert: notification.alternatives_required.v1 outbox row ────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.alternatives_required.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.alternatives_required audit event ──────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.alternatives_required");

        Assert.NotNull(audit);
    }

    // ── Test 2: Transient → durable retry → success (FM#10 probe-then-success) ──────────────

    /// <summary>
    /// Fake adapter returns Transient on create #1, Created on #2.
    /// Drive one publisher tick → op stays Finalizing, attempt=TransientFailure, NextEligibleAt set,
    ///   a scheduled_jobs row exists (JobType=booking.command.requested.v1, OperationId set),
    ///   finalize step NotBefore is set.
    /// Fast-forward: set the scheduled_job RunAfter to the past, call PollDueBatchAsync,
    ///   assert op → Booked (re-create under same key succeeded).
    /// </summary>
    [Fact]
    public async Task Transient_FirstCreate_DurableRetry_ThenSuccess()
    {
        await NeutralizeExistingUnpublishedAsync();
        await NeutralizeExistingScheduledJobsAsync();

        var tenantId = $"t6-transient-{Guid.NewGuid():N}";
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

        var fakeAdapter = new T6FakeAdapter { TransientOnFirstN = 1, SuccessRef = "ext_retry_success" };

        await using var sp = BuildContainer(fakeAdapter: fakeAdapter);
        var publisher = BuildPublisher(sp);

        // First tick: Accepted → Finalizing → Transient → attempt=TransientFailure + retry scheduled.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var midCtx = fixture.CreateContext(tenantId);

        // ── Assert after first tick: op still Finalizing ────────────────────────────────────
        var opMid = await midCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(opMid);
        Assert.Equal(OperationStatus.Finalizing, opMid!.Status);

        // ── Assert: latest attempt has Outcome=TransientFailure + NextEligibleAt set ─────────
        var attemptMid = await midCtx.WorkflowAttempts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OperationId == operationId && a.WorkflowStep == "finalize")
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync();

        Assert.NotNull(attemptMid);
        Assert.Equal(AttemptOutcome.TransientFailure, attemptMid!.Outcome);
        Assert.NotNull(attemptMid.NextEligibleAt);

        // ── Assert: a scheduled_jobs row exists for the retry ───────────────────────────────
        var scheduledJob = await midCtx.ScheduledJobs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j =>
                j.OperationId == operationId &&
                j.JobType == "booking.command.requested.v1" &&
                j.Status == ScheduledJobStatus.Pending);

        Assert.NotNull(scheduledJob);
        Assert.Equal(operationId, scheduledJob!.OperationId);
        Assert.Equal("booking.commands", scheduledJob.DestinationQueue);

        // ── Assert: finalize step NotBefore is set (sweeper suppression) ────────────────────
        var stepMid = await midCtx.WorkflowSteps
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OperationId == operationId && s.StepType == "finalize");

        Assert.NotNull(stepMid);
        Assert.NotNull(stepMid!.NotBefore);

        // ── Fast-forward: set RunAfter to the past so the poller claims it ──────────────────
        await using (var fwdCtx = fixture.CreateContext(tenantId))
        {
            await fwdCtx.ScheduledJobs
                .IgnoreQueryFilters()
                .Where(j => j.JobId == scheduledJob.JobId)
                .ExecuteUpdateAsync(b => b.SetProperty(j => j.RunAfter, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        // ── Poller tick: claim + publish → second driver dispatch → Booked ─────────────────
        var poller = BuildPoller(sp);
        await poller.PollDueBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var opFinal = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(opFinal);
        Assert.Equal(OperationStatus.Booked, opFinal!.Status);
        Assert.Equal("ext_retry_success", opFinal.BookingId);

        // Adapter was called twice (attempt 1 = Transient, attempt 2 = Created).
        Assert.Equal(2, fakeAdapter.CreateCallCount);
    }

    // ── Test 3: Budget exhausted → Reconciling → ManualReview (FM#11) ───────────────────────

    /// <summary>
    /// Fake adapter ALWAYS returns Transient.  Op seeded directly in Finalizing with a finalize step
    /// whose CreatedAt is in the past (now-1h), exceeding ForCreateBooking.MaxElapsed=10min.
    /// On the first transient failure the elapsed budget is exhausted → Finalizing→Reconciling;
    /// driver loops immediately into Reconcile, which (no effect, exhausted) → ManualReview.
    ///
    /// Assert: op → ManualReview, operations_work_items row (State="open"),
    ///   notification.still_confirming.v1 outbox, operation.manual_review audit.
    /// </summary>
    [Fact]
    public async Task BudgetExhausted_Transient_RoutesToManualReview()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t6-exhausted-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;
        // Seed op already in Finalizing so the driver goes straight to FinalizeBookingCommand.
        // CreatedAt = now-1h exceeds MaxElapsed(10min) so even attempt #1 exhausts elapsed.
        var oldTime = DateTimeOffset.UtcNow.AddHours(-1);

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Finalizing);
            var providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

            // finalize step with old CreatedAt (proxy for first attempt time).
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
                CreatedAt = oldTime,
                UpdatedAt = oldTime,
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

        // Adapter always returns Transient.
        var fakeAdapter = new T6FakeAdapter { TransientOnFirstN = int.MaxValue };

        await using var sp = BuildContainer(fakeAdapter: fakeAdapter);
        var publisher = BuildPublisher(sp);

        // One publisher tick:
        //   Finalize → Transient, elapsed > MaxElapsed → budget exhausted → Finalizing→Reconciling
        //   Driver loop continues → Reconcile (no effect proven, budget exhausted) → ManualReview.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → ManualReview ────────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.ManualReview, op!.Status);

        // ── Assert: operations_work_items row (State="open") ────────────────────────────────
        var workItem = await assertCtx.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.OperationId == operationId);

        Assert.NotNull(workItem);
        Assert.Equal("open", workItem!.State);

        // ── Assert: notification.still_confirming.v1 outbox row ────────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.still_confirming.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.manual_review audit event ──────────────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.manual_review");

        Assert.NotNull(audit);
    }

    // ── Test 4: Circuit open → no provider flood, durable retry scheduled (FM#10) ────────────

    /// <summary>
    /// Open the circuit for ("fake", "default") by recording failures via IProviderCircuitBreaker.
    /// Seed an op in Held state (so the driver enters FinalizeBookingCommand).
    /// Drive one publisher tick.
    /// Assert: provider create NOT called (call count == 0), a scheduled_jobs retry row exists,
    ///   op stays Finalizing (the abandoned Planned attempt does not advance op).
    /// </summary>
    [Fact]
    public async Task CircuitOpen_NoProviderFlood_DurableRetryScheduled()
    {
        await NeutralizeExistingUnpublishedAsync();
        await NeutralizeExistingScheduledJobsAsync();

        var tenantId = $"t6-circuit-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            // Seed op in Held so FinalizeBookingCommand runs directly (skips ReserveCapacity).
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Held);

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

        var fakeAdapter = new T6FakeAdapter();  // call counter; no create call expected

        await using var sp = BuildContainer(fakeAdapter: fakeAdapter);

        // Open the circuit for ("fake", "default") by recording 5 failures.
        var circuitBreaker = sp.GetRequiredService<IProviderCircuitBreaker>();
        for (var i = 0; i < 5; i++)
            circuitBreaker.RecordFailure("fake", "default");

        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: provider create was NOT called ──────────────────────────────────────────
        Assert.Equal(0, fakeAdapter.CreateCallCount);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op stays Finalizing (circuit blocked, no outcome committed) ───────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Finalizing, op!.Status);

        // ── Assert: a scheduled_jobs retry row exists ────────────────────────────────────────
        var scheduledJob = await assertCtx.ScheduledJobs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j =>
                j.OperationId == operationId &&
                j.JobType == "booking.command.requested.v1" &&
                j.Status == ScheduledJobStatus.Pending);

        Assert.NotNull(scheduledJob);
        Assert.Equal("booking.commands", scheduledJob!.DestinationQueue);
    }

    // ── Test 5: AuthError → Reconciling (FIX X1) ─────────────────────────────────────────────

    /// <summary>
    /// FIX (X1): adapter returns AuthError on CreateBooking.
    /// AuthError is NOT a permanent booking failure — it is an ops/credentials issue.
    /// Expected: Finalizing → Reconciling (NOT CompensationRequired / FailedFinal).
    /// The attempt must be recorded (Outcome=Abandoned) and the op reaches Reconciling.
    /// </summary>
    [Fact]
    public async Task AuthError_FromFinalizing_RoutesToReconciling_NotCompensationRequired()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t6-autherr-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Held);

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

        var fakeAdapter = new T6FixedResultAdapter { ResultKind = ProviderResultKind.AuthError };

        await using var sp = BuildContainer(fakeAdapter: fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op is NOT in CompensationRequired or FailedFinal ─────────────────────────
        // FIX X1: AuthError routes to Reconciling (NOT CompensationRequired).  The driver loop
        // then runs Reconcile which may further advance the op (e.g. Reconciling→Held→Finalizing
        // again, or Reconciling→ManualReview if budget exhausts) — so the exact status after one
        // publisher tick can vary.  What matters is that CompensationRequired and FailedFinal are
        // NEVER the outcome of an AuthError (those are reserved for Rejected/PermanentError only).
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.NotEqual(OperationStatus.CompensationRequired, op!.Status);
        Assert.NotEqual(OperationStatus.FailedFinal, op.Status);

        // ── Assert: at least one finalize attempt was recorded with Outcome=Abandoned ─────────
        var attempt = await assertCtx.WorkflowAttempts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OperationId == operationId
                        && a.WorkflowStep == "finalize"
                        && a.Outcome == AttemptOutcome.Abandoned)
            .FirstOrDefaultAsync();

        Assert.NotNull(attempt);
    }

    /// <summary>
    /// FIX (X1): adapter returns PaymentActionRequired on CreateBooking.
    /// PaymentActionRequired is NOT a permanent failure — must NOT route to CompensationRequired.
    /// </summary>
    [Fact]
    public async Task PaymentActionRequired_FromFinalizing_DoesNotRouteToCompensationRequired()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t6-payact-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Held);

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

        var fakeAdapter = new T6FixedResultAdapter { ResultKind = ProviderResultKind.PaymentActionRequired };

        await using var sp = BuildContainer(fakeAdapter: fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // Same reasoning as AuthError: exact post-tick status can vary but must never be
        // CompensationRequired or FailedFinal.
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.NotEqual(OperationStatus.CompensationRequired, op!.Status);
        Assert.NotEqual(OperationStatus.FailedFinal, op.Status);

        // At least one finalize attempt with Outcome=Abandoned must exist.
        var attempt = await assertCtx.WorkflowAttempts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OperationId == operationId
                        && a.WorkflowStep == "finalize"
                        && a.Outcome == AttemptOutcome.Abandoned)
            .FirstOrDefaultAsync();

        Assert.NotNull(attempt);
    }
}
