#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using System.Text.Json;
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Application.Handlers.BookingFulfillment.Reconcile;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Capacity;

// ── Fake booking provider adapter ────────────────────────────────────────────────────────────

/// <summary>
/// Minimal fake adapter for T7 finalize-against-hold tests.
/// Always returns Created so the finalize success TXN2 path is exercised.
/// </summary>
file sealed class T7FakeBookingProviderAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake_t7";

    public ProviderResultKind CreateResult { get; set; } = ProviderResultKind.Created;
    public string CreateRef { get; set; } = "fake_booking_t7";

    // Reconcile's §16.2 ladder asks these. Default stays NotFound so every existing test keeps
    // its behaviour; the reconcile-against-hold tests set them to prove an effect exists.
    public ProviderResultKind LookupResult { get; set; } = ProviderResultKind.NotFound;
    public string? LookupRef { get; set; }

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
        => Task.FromResult(CreateResult == ProviderResultKind.Created
            ? new ProviderResult(ProviderResultKind.Created, CreateRef)
            : new ProviderResult(CreateResult));

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(
        string providerAccount, string providerIdempotencyKey, CancellationToken ct = default)
        => Task.FromResult(LookupRef is null
            ? new ProviderResult(LookupResult)
            : new ProviderResult(LookupResult, LookupRef));

    public Task<ProviderResult> LookupByReferenceAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> ModifyBookingAsync(
        string providerAccount, string providerReference, ProviderBookingRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.Created, providerReference));

    public Task<ProviderResult> CancelBookingAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.Created));
}

// ── Test class ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for T7: finalize-against-hold integration in
/// <see cref="Cordia.Application.Handlers.BookingFulfillment.Finalize.FinalizeBookingCommandHandler"/>
/// and hold release on compensation (spec 07 §6.1, §6.2, §14.3, T7).
///
/// Scenarios:
///   1. Happy path — first-party reserve → finalize → slot held→booked, hold Active→Finalized,
///      op Booked, booking_confirmed emitted (§6.1).
///   2. Expiry races finalize (§6.2) — hold released (Expired) BEFORE finalize runs →
///      FinalizeAgainstHold sees rows==0 → op routed to Reconciling, slot booked stays 0,
///      no booking_confirmed emitted.
///   3. Non-tracked finalize unchanged — op in Finalizing with NO hold/slot → Booked as before
///      (regression guard).
///   4. Compensation frees the hold — op in CompensationRequired, active hold + slot →
///      CompensateOperationCommand → op FailedFinal, hold Released, slot held==0.
///
/// Each test uses a unique tenantId; assertions are on own ids only (shared test DB).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class FinalizeAgainstHoldTests(PostgresFixture fixture)
{
    // ── Container builder ─────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer(string tenantId, IBookingProviderAdapter fakeAdapter)
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
        services.ScopeOutboxClaimToTenant(tenantId);

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

    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    // ── Seeding helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a capacity slot (total=1, held=1), an active capacity hold (WorkflowStep="create",
    /// SlotId, ReservedCapacity=1), an intent + revision with canonical payload, and a booking
    /// operation in the given status.  The intent revision canonical payload has party_size=1
    /// so FinalizeBookingCommandHandler can load it without failing.
    ///
    /// Returns (slotId, operationId, holdId).
    /// </summary>
    private async Task<(string slotId, string operationId, string holdId)> SeedFinalizeWithHoldAsync(
        string tenantId,
        OperationStatus opStatus = OperationStatus.Finalizing)
    {
        var resourceId = $"res-{Guid.NewGuid():N}";
        var requestedAt = DateTimeOffset.UtcNow.AddHours(1);
        var slotId = $"{resourceId}|{requestedAt:O}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var holdId = $"hold_{Guid.NewGuid():N}";

        var canonicalPayload = JsonSerializer.Serialize(new
        {
            party_size = 1,
            resource_id = resourceId,
            starts_at = requestedAt.ToString("O"),
        });

        await using var ctx = fixture.CreateContext(tenantId);

        // Slot: total=1, held=1 (as if reserve already ran).
        ctx.CapacitySlots.Add(new CapacitySlot
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

        // Active hold.
        ctx.CapacityHolds.Add(new CapacityHold
        {
            TenantId = tenantId,
            HoldId = holdId,
            OperationId = operationId,
            WorkflowStep = "create",
            SlotId = slotId,
            ReservedCapacity = 1,
            Status = HoldStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Version = 0,
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
            Status = opStatus,
            Version = 0,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        ctx.BookingOperations.Add(operation);
        await ctx.SaveChangesAsync();

        return (slotId, operationId, holdId);
    }

    /// <summary>
    /// Seeds a finalize WorkflowStep (InProgress) and a prior TransientFailure attempt
    /// for the given operation — so all early FinalizeBookingCommandHandler guards
    /// (idempotent shortcut, Inv-7, cancel-before-create) fall through to TXN1.
    /// </summary>
    private async Task SeedFinalizeStepAndAttemptAsync(string tenantId, string operationId)
    {
        var providerKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1);

        await using var ctx = fixture.CreateContext(tenantId);

        ctx.WorkflowSteps.Add(new WorkflowStep
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

        ctx.WorkflowAttempts.Add(new WorkflowAttempt
        {
            TenantId = tenantId,
            AttemptId = $"att_{Guid.NewGuid():N}",
            OperationId = operationId,
            WorkflowStep = "finalize",
            ProviderIdempotencyKey = providerKey,
            ProviderName = "fake_t7",
            ProviderAccount = "default",
            RequestHash = "h",
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.TransientFailure,
            RequestMeta = "{}",
            ResponseMeta = "{}",
        });

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Adds an outbox trigger message so the OutboxPublisherHostedService drives the op.
    /// </summary>
    private async Task SeedOutboxTriggerAsync(string tenantId, string operationId, string messageId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            MessageType = "booking.command.requested.v1",
            CorrelationId = operationId,
            OperationId = operationId,
            Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    // ── Scenario 1: Happy path finalize against hold (§6.1) ──────────────────────────────────

    /// <summary>
    /// §6.1 happy path: slot(total=1, held=1) + active hold + op in Finalizing → finalize
    /// (stub provider returns Created) → FinalizeAgainstOperationAsync Finalized →
    ///   - op → Booked
    ///   - hold status → Finalized
    ///   - slot held==0, booked==1
    ///   - notification.booking_confirmed.v1 outbox row emitted
    /// </summary>
    [Fact]
    public async Task HappyPath_FinalizeAgainstHold_SlotHeldToBooked_OpBooked_ConfirmedEmitted()
    {
        var tenantId = $"t7-fp-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        var (slotId, operationId, holdId) = await SeedFinalizeWithHoldAsync(tenantId, OperationStatus.Finalizing);
        await SeedFinalizeStepAndAttemptAsync(tenantId, operationId);
        await SeedOutboxTriggerAsync(tenantId, operationId, messageId);

        var fakeAdapter = new T7FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
            CreateRef = $"book-t7-fp-{Guid.NewGuid():N}",
        };

        await using var sp = BuildContainer(tenantId, fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // op → Booked.
        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Booked, op.Status);

        // hold → Finalized.
        var hold = await assertCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Finalized, hold.Status);
        Assert.Equal(ReleaseReason.Finalized, hold.ReleaseReason);

        // slot: held==0, booked==1.
        var slot = await assertCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);
        Assert.Equal(1, slot.BookedCapacity);

        // notification.booking_confirmed.v1 outbox row exists.
        var confirmed = await assertCtx.OutboxMessages.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");
        Assert.NotNull(confirmed);
    }

    // ── Scenario 2: Expiry races finalize (§6.2) ─────────────────────────────────────────────

    /// <summary>
    /// §14.3 / FIX 2 race: hold is released (Expired) BEFORE finalize runs.
    /// FinalizeAgainstOperationAsync sees Expired status → HoldNoLongerValid →
    ///   - FinalizeBookingCommandHandler routes op → CompensationRequired (NOT Reconciling, NOT Booked)
    ///     because the provider effect exists but capacity is gone; Compensate will cancel the booking.
    ///   - slot booked stays 0 (no double decrement — expiry already freed held)
    ///   - NO notification.booking_confirmed.v1 emitted
    ///   - operation.hold_no_longer_valid audit event is emitted
    ///
    /// The handler is driven DIRECTLY via IMediator (one call) so we assert the immediate
    /// routing decision without interference from the subsequent Compensate step.
    /// </summary>
    [Fact]
    public async Task ExpiryRacesFinalize_HoldNoLongerValid_OpRouted_ToCompensationRequired_NoDoubleDecrement_NoConfirmed()
    {
        var tenantId = $"t7-race-{Guid.NewGuid():N}";

        var (slotId, operationId, holdId) = await SeedFinalizeWithHoldAsync(tenantId, OperationStatus.Finalizing);
        await SeedFinalizeStepAndAttemptAsync(tenantId, operationId);

        // Simulate expiry winning the race: release the hold BEFORE the finalize driver runs.
        await using (var expireCtx = fixture.CreateContext(tenantId))
        {
            var holdRepoLocal = new CapacityHoldRepository(expireCtx);
            var svc = new FirstPartyCapacityReservation(expireCtx, holdRepoLocal, new CapacityLockOrder(new CapacityHoldRepository(expireCtx), new CapacitySlotRepository(expireCtx)), new Cordia.Infrastructure.Observability.Metrics());

            await using var tx = await expireCtx.Database.BeginTransactionAsync();
            await svc.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired);
            await tx.CommitAsync();
        }

        // Verify: slot held is now 0 after expiry.
        await using (var preCtx = fixture.CreateContext(tenantId))
        {
            var preSlot = await preCtx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, preSlot.HeldCapacity);
        }

        // Drive FinalizeBookingCommandHandler ONE TIME directly via IMediator (not the full
        // publisher loop which also invokes Reconcile and re-routes the op further).
        var fakeAdapter = new T7FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
        };

        StepOutcome finalizeOutcome;
        await using (var sp = BuildContainer(tenantId, fakeAdapter))
        {
            using var scope = sp.CreateScope();
            var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantCtx.SetTenant(tenantId);

            var mediator = scope.ServiceProvider.GetRequiredService<MediatR.IMediator>();
            // Drive Advance which dispatches Finalize for the Finalizing op.
            finalizeOutcome = await mediator.Send(
                new FinalizeBookingCommand(tenantId, operationId),
                CancellationToken.None);
        }

        // Handler must return Continue (→CompensationRequired path detected — FIX 2).
        Assert.Equal(StepOutcome.Continue, finalizeOutcome);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // op → CompensationRequired (finalize routed here due to HoldNoLongerValid — FIX 2).
        // Provider effect exists but capacity is gone → Compensate will cancel the provider booking.
        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.CompensationRequired, op.Status);

        // slot booked stays 0 (no double-decrement — expiry already freed held).
        var slot = await assertCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);

        // NO booking_confirmed emitted.
        var confirmed = await assertCtx.OutboxMessages.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");
        Assert.Null(confirmed);

        // operation.hold_no_longer_valid audit event emitted.
        var auditEvt = await assertCtx.AuditEvents.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.hold_no_longer_valid");
        Assert.NotNull(auditEvt);
    }

    // ── Scenario 3: Non-tracked finalize unchanged (regression guard) ─────────────────────────

    /// <summary>
    /// Regression guard: op in Finalizing with NO hold and NO slot row → finalize → Booked
    /// (the non-tracked path must be completely unchanged by T7).
    /// </summary>
    [Fact]
    public async Task NonTrackedFinalize_NoHold_TransitionsToBooked_Unchanged()
    {
        var tenantId = $"t7-notrack-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = await SeedNonTrackedFinalizingOpAsync(tenantId);

        await SeedFinalizeStepAndAttemptAsync(tenantId, operationId);
        await SeedOutboxTriggerAsync(tenantId, operationId, messageId);

        var fakeAdapter = new T7FakeBookingProviderAdapter
        {
            CreateResult = ProviderResultKind.Created,
            CreateRef = $"book-t7-notrack-{Guid.NewGuid():N}",
        };

        await using var sp = BuildContainer(tenantId, fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // op → Booked (non-tracked path unchanged).
        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Booked, op.Status);

        // No holds (no slot seeded → no hold created).
        var holds = await assertCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.OperationId == operationId)
            .ToListAsync();
        Assert.Empty(holds);

        // booking_confirmed emitted.
        var confirmed = await assertCtx.OutboxMessages.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");
        Assert.NotNull(confirmed);
    }

    private async Task<string> SeedNonTrackedFinalizingOpAsync(string tenantId)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);

        // Canonical payload with no resource dimension → not tracked.
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
            CanonicalPayload = "{\"party_size\":1}",
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
            Status = OperationStatus.Finalizing,
            Version = 0,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        ctx.BookingOperations.Add(operation);
        await ctx.SaveChangesAsync();

        return operationId;
    }

    // ── Scenario 4: Compensation frees the hold ───────────────────────────────────────────────

    /// <summary>
    /// §6.1 T7: op in CompensationRequired with an active hold (slot total=1, held=1) and NO
    /// providerRef (4b path: nothing was created) → CompensateOperationCommand →
    ///   - op → FailedFinal
    ///   - hold → Released (status Released, reason Compensated)
    ///   - slot held == 0
    /// </summary>
    [Fact]
    public async Task Compensation_FreesActiveHold_SlotHeldZero_OpFailedFinal()
    {
        var tenantId = $"t7-comp-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        // Seed slot + hold + op in CompensationRequired (no finalize attempt providerRef).
        var (slotId, operationId, holdId) = await SeedFinalizeWithHoldAsync(tenantId, OperationStatus.CompensationRequired);

        // Seed outbox trigger for CompensateOperationCommand.
        await SeedOutboxTriggerAsync(tenantId, operationId, messageId);

        // Fake adapter CancelBookingAsync returns Created (cancel acknowledged — though it won't
        // be called because providerRef is null in the 4b path).
        var fakeAdapter = new T7FakeBookingProviderAdapter();

        await using var sp = BuildContainer(tenantId, fakeAdapter);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // op → FailedFinal.
        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.FailedFinal, op.Status);

        // hold → Released (Compensated reason → HoldStatus.Released).
        var hold = await assertCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Released, hold.Status);
        Assert.Equal(ReleaseReason.Compensated, hold.ReleaseReason);

        // slot held == 0.
        var slot = await assertCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);
    }

    // ── Reconcile against a hold (#59 part 1 + U1) ───────────────────────────────────────────

    /// <summary>
    /// The whole Booked write-set, on the reconcile lane, against a real database.
    ///
    /// <para>Before this, <c>ReconcileOperationCommandHandler</c> transitioned an operation to
    /// <c>Booked</c> while doing neither of the two things that make that outcome real: it never
    /// settled the capacity hold, and it never wrote a <c>bookings</c> row. Nothing in the suite
    /// could see it — <c>ReconciliationTests</c> contains no <c>CapacitySlot</c> or
    /// <c>CapacityHold</c> reference at all, and the unit suite had no capacity mock.</para>
    ///
    /// <para>Asserts all three ledger facts plus the booking row, because each fails
    /// independently: the hold could finalize without the slot moving, and both could be right
    /// while the booking row is still missing.</para>
    /// </summary>
    [Fact]
    public async Task ReconcileAgainstHold_SettlesCapacity_AndMaterialisesTheBooking()
    {
        var tenantId = $"t7-rec-{Guid.NewGuid():N}";
        var providerRef = $"book-t7-rec-{Guid.NewGuid():N}";

        var (slotId, operationId, holdId) =
            await SeedFinalizeWithHoldAsync(tenantId, OperationStatus.Reconciling);
        await SeedFinalizeStepAndAttemptAsync(tenantId, operationId);

        // The provider turns out to hold the booking after all.
        var fakeAdapter = new T7FakeBookingProviderAdapter
        {
            LookupResult = ProviderResultKind.AlreadyExists,
            LookupRef = providerRef,
        };

        StepOutcome outcome;
        await using (var sp = BuildContainer(tenantId, fakeAdapter))
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

            outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new ReconcileOperationCommand(tenantId, operationId), CancellationToken.None);
        }

        Assert.Equal(StepOutcome.Terminal, outcome);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Booked, op.Status);

        // (1) hold Active → Finalized.
        var hold = await assertCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters().SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Finalized, hold.Status);

        // (2) slot held → booked. This is the pair that used to drift: held was later swept to 0
        // by expiry while booked stayed 0, so the seat read as free under a Booked operation.
        var slot = await assertCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);
        Assert.Equal(1, slot.BookedCapacity);

        // (3) the bookings row the console and both mutation doors read (U1).
        var booking = await assertCtx.Bookings.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(b => b.TenantId == tenantId && b.BookingId == providerRef);
        Assert.Equal(providerRef, booking.ProviderReference);
        Assert.Equal(BookingLifecycleStatus.Confirmed, booking.LifecycleStatus);
    }

    /// <summary>
    /// The hold is gone before reconcile proves the effect: a booking exists at the provider with
    /// no capacity behind it. Finalize routes this to CompensationRequired, but that edge does not
    /// exist from Reconciling — attempting it throws and the driver dead-letters the operation,
    /// which is what #59's filed remedy would have done. Q1 chose ManualReview.
    /// </summary>
    [Fact]
    public async Task ReconcileAgainstHold_HoldAlreadyReleased_EscalatesAndDoesNotBook()
    {
        var tenantId = $"t7-rec-gone-{Guid.NewGuid():N}";
        var providerRef = $"book-t7-gone-{Guid.NewGuid():N}";

        var (slotId, operationId, holdId) =
            await SeedFinalizeWithHoldAsync(tenantId, OperationStatus.Reconciling);
        await SeedFinalizeStepAndAttemptAsync(tenantId, operationId);

        await using (var expireCtx = fixture.CreateContext(tenantId))
        {
            var svc = new FirstPartyCapacityReservation(
                expireCtx,
                new CapacityHoldRepository(expireCtx),
                new CapacityLockOrder(new CapacityHoldRepository(expireCtx), new CapacitySlotRepository(expireCtx)),
                new Cordia.Infrastructure.Observability.Metrics());

            await using var tx = await expireCtx.Database.BeginTransactionAsync();
            await svc.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired);
            await tx.CommitAsync();
        }

        var fakeAdapter = new T7FakeBookingProviderAdapter
        {
            LookupResult = ProviderResultKind.AlreadyExists,
            LookupRef = providerRef,
        };

        StepOutcome outcome;
        await using (var sp = BuildContainer(tenantId, fakeAdapter))
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

            outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new ReconcileOperationCommand(tenantId, operationId), CancellationToken.None);
        }

        Assert.Equal(StepOutcome.Wait, outcome);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.ManualReview, op.Status);

        // No booking row and no confirmation for an operation with no capacity behind it.
        Assert.False(await assertCtx.Bookings.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(b => b.TenantId == tenantId && b.BookingId == providerRef));

        Assert.False(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId
                && m.OperationId == operationId
                && m.MessageType == "notification.booking_confirmed.v1"));

        // Expiry already freed the held seat; reconcile must not decrement it a second time.
        var slot = await assertCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);
    }
}
