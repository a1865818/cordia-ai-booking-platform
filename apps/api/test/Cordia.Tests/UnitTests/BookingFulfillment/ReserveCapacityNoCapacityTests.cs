using System.Text.Json;
using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ReserveCapacityCommandHandler"/> (spec 07 T5).
///
/// Tests the three routing branches:
///   A. Tracked + Reserved → Held + expiry scheduled.
///   B. Tracked + NoCapacity → AlternativesRequired + outbox.
///   C. NOT tracked (no slot row) → Held, no hold, no expiry.
/// </summary>
[Trait("Category", "Unit")]
public class ReserveCapacityNoCapacityTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<ICapacityReservation> _capacityReservation = new();
    private readonly Mock<ICapacitySlotRepository> _slotRepo = new();
    private readonly Mock<IBookingIntentRepository> _intentRepo = new();
    private readonly Mock<IScheduledJobRepository> _scheduledJobRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ReserveCapacityNoCapacityTests()
    {
        // UoW executes work immediately (mirrors the real behavior inline).
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Default step repo: no existing step, AddAsync succeeds.
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(
                It.IsAny<string>(), It.IsAny<string>(), "reserve_capacity", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _stepRepo.Setup(r => r.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default scheduled job repo: AddAsync succeeds.
        _scheduledJobRepo.Setup(r => r.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ReserveCapacityCommandHandler CreateHandler(TimeSpan? holdTtl = null, Metrics? metrics = null)
    {
        var options = Options.Create(new CapacityOptions
        {
            HoldTtl = holdTtl ?? TimeSpan.FromMinutes(30),
        });
        var expiryScheduler = new CapacityExpiryScheduler(_scheduledJobRepo.Object);

        return new ReserveCapacityCommandHandler(
            _opRepo.Object,
            _stepRepo.Object,
            _capacityReservation.Object,
            _slotRepo.Object,
            _intentRepo.Object,
            expiryScheduler,
            _outboxRepo.Object,
            _auditRepo.Object,
            _uow.Object,
            options,
            metrics ?? new Metrics());
    }

    private static BookingOperation MakeOp(
        string tenantId,
        string opId,
        OperationStatus status,
        long version = 0,
        string intentId = "int-1",
        int intentRevision = 1) =>
        new()
        {
            TenantId = tenantId,
            OperationId = opId,
            IntentId = intentId,
            IntentRevision = intentRevision,
            Status = status,
            Version = version,
        };

    private static BookingIntentRevision MakeRevision(
        string tenantId,
        string intentId,
        string resourceId,
        DateTimeOffset requestedAt,
        int partySize = 2) =>
        new()
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = JsonSerializer.Serialize(new
            {
                party_size = partySize,
                resource_id = resourceId,
                starts_at = requestedAt.ToString("O"),
            }),
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        };

    private static CapacityHold MakeHold(string tenantId, string holdId, string slotId) =>
        new()
        {
            TenantId = tenantId,
            HoldId = holdId,
            OperationId = "op-1",
            WorkflowStep = "create",
            SlotId = slotId,
            ReservedCapacity = 2,
            Status = HoldStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Version = 0,
        };

    // ── Test A: tracked + Reserved → Held + expiry scheduled ────────────────────────────────

    /// <summary>
    /// Tracked slot + ReserveAsync returns Held → op transitions to Held and
    /// a hold-expiry scheduled job is inserted.
    /// </summary>
    [Fact]
    public async Task TrackedSlot_Reserved_TransitionsToHeld_AndSchedulesExpiry()
    {
        var (tenantId, opId, intentId) = ("t-res-a", "op-res-a", "int-res-a");
        var resourceId = "r1";
        var requestedAt = new DateTimeOffset(2026, 7, 1, 19, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{requestedAt:O}";
        var holdId = "hold_abc123";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);
        var rev = MakeRevision(tenantId, intentId, resourceId, requestedAt, partySize: 2);
        var hold = MakeHold(tenantId, holdId, slotId);

        // First call (before txn) + second call (inside txn).
        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        // Slot row EXISTS → tracked.
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = requestedAt,
                TotalCapacity = 5,
                Version = 0,
            });

        // Reserve returns Held.
        _capacityReservation.Setup(r => r.ReserveAsync(
                It.Is<ReserveRequest>(req => req.TenantId == tenantId && req.SlotId == slotId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.Held(hold));

        // Transition succeeds (1 row updated).
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Assert: result is Continue.
        Assert.Equal(StepOutcome.Continue, result);

        // Assert: transition to Held was requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Assert: expiry job was inserted with correct job type.
        _scheduledJobRepo.Verify(r => r.AddAsync(
            It.Is<ScheduledJob>(j =>
                j.TenantId == tenantId &&
                j.JobType == "booking.scheduled.hold_expiry.v1" &&
                j.DestinationQueue == "booking.scheduled" &&
                j.OperationId == opId &&
                j.Payload.Contains(holdId)),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert: NO AlternativesRequired transition.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test B: tracked + NoCapacity → AlternativesRequired + outbox ────────────────────────

    /// <summary>
    /// Tracked slot + ReserveAsync returns NoCapacity → op transitions to AlternativesRequired
    /// + notification.alternatives_required.v1 outbox added, result=Wait.
    /// </summary>
    [Fact]
    public async Task TrackedSlot_NoCapacity_TransitionsToAlternativesRequired_AddsOutbox()
    {
        var (tenantId, opId, intentId) = ("t-nocap-b", "op-nocap-b", "int-nocap-b");
        var resourceId = "r2";
        var requestedAt = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{requestedAt:O}";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);
        var rev = MakeRevision(tenantId, intentId, resourceId, requestedAt, partySize: 2);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        // Slot row EXISTS → tracked.
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = requestedAt,
                TotalCapacity = 1,
                HeldCapacity = 1,
                Version = 0,
            });

        // Reserve returns NoCapacity.
        _capacityReservation.Setup(r => r.ReserveAsync(
                It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.NoCapacity());

        // Transition to AlternativesRequired succeeds (1 row).
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Returns Wait (AlternativesRequired requires external user input).
        Assert.Equal(StepOutcome.Wait, result);

        // AlternativesRequired transition requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Outbox notification added.
        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m =>
                m.TenantId == tenantId &&
                m.OperationId == opId &&
                m.MessageType == "notification.alternatives_required.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        // NO expiry job enqueued.
        _scheduledJobRepo.Verify(r => r.AddAsync(
            It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test C: NOT tracked (no slot row) → Held, no hold, no expiry ────────────────────────

    /// <summary>
    /// When the slot is not provisioned (slotRepo.GetAsync returns null), the handler
    /// transitions to Held WITHOUT calling ReserveAsync and WITHOUT scheduling an expiry job.
    /// This preserves the current always-available behavior for un-provisioned resources.
    /// </summary>
    [Fact]
    public async Task NotTrackedSlot_TransitionsToHeld_NoReserveCall_NoExpiry()
    {
        var (tenantId, opId, intentId) = ("t-untracked-c", "op-untracked-c", "int-untracked-c");
        var resourceId = "r3";
        var requestedAt = new DateTimeOffset(2026, 7, 3, 14, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{requestedAt:O}";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);
        var rev = MakeRevision(tenantId, intentId, resourceId, requestedAt, partySize: 1);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        // Slot row does NOT exist → not tracked.
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapacitySlot?)null);

        // Transition to Held succeeds.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Returns Continue.
        Assert.Equal(StepOutcome.Continue, result);

        // Transition to Held was requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // ReserveAsync was NEVER called (non-tracked path skips capacity check).
        _capacityReservation.Verify(r => r.ReserveAsync(
            It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // No expiry job inserted.
        _scheduledJobRepo.Verify(r => r.AddAsync(
            It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Backwards-compat: no resource dimension → non-tracked → Held ───────────────────────

    /// <summary>
    /// When the canonical payload is empty (no resource/time dimensions), slotId=null,
    /// the handler transitions to Held as a non-tracked op (availability mode = live).
    /// </summary>
    [Fact]
    public async Task EmptyPayload_NonTracked_TransitionsToHeld_NoReserveCall()
    {
        var (tenantId, opId, intentId) = ("t-empty-d", "op-empty-d", "int-empty-d");

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);
        var rev = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        };

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);

        // slotRepo was never called (slotId null → skip lookup).
        _slotRepo.Verify(r => r.GetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        _capacityReservation.Verify(r => r.ReserveAsync(
            It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        _scheduledJobRepo.Verify(r => r.AddAsync(
            It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── FIX 1: starts_at is read as the primary slot-time field ─────────────────────────────

    /// <summary>
    /// FIX 1: ParseCanonicalPayload reads starts_at first (canonical agent field).
    /// A payload with starts_at but no requested_at must derive a slot key and reach the
    /// tracked path (slot found → ReserveAsync called).
    /// </summary>
    [Fact]
    public async Task StartsAt_IsReadFirst_TrackedPathFires()
    {
        var (tenantId, opId, intentId) = ("t-startsat-e", "op-startsat-e", "int-startsat-e");
        var resourceId = "r5";
        var startsAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{startsAt:O}";
        var holdId = "hold_starts_at_test";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);

        // Revision uses starts_at only — no requested_at (canonical agent payload format).
        var rev = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = JsonSerializer.Serialize(new
            {
                party_size = 1,
                resource_id = resourceId,
                starts_at = startsAt.ToString("O"),
                // NO requested_at
            }),
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        };
        var hold = MakeHold(tenantId, holdId, slotId);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        // Slot row EXISTS → tracked.
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = startsAt,
                TotalCapacity = 2,
                Version = 0,
            });

        // Reserve returns Held.
        _capacityReservation.Setup(r => r.ReserveAsync(
                It.Is<ReserveRequest>(req => req.TenantId == tenantId && req.SlotId == slotId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.Held(hold));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Tracked path fires: ReserveAsync was called with the starts_at-derived slotId.
        Assert.Equal(StepOutcome.Continue, result);

        _capacityReservation.Verify(r => r.ReserveAsync(
            It.Is<ReserveRequest>(req => req.SlotId == slotId),
            It.IsAny<CancellationToken>()), Times.Once);

        _scheduledJobRepo.Verify(r => r.AddAsync(
            It.Is<ScheduledJob>(j => j.Payload.Contains(holdId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression (live-smoke): a real canonical payload serializes an ABSENT party_size as
    /// explicit JSON <c>null</c> (also duration_minutes/target_booking_version). JsonElement.TryGetInt32
    /// throws InvalidOperationException (NOT JsonException) on a non-Number element, so the parse must
    /// guard ValueKind == Number first. This payload must parse cleanly (capacity defaults to 1) and
    /// reach the tracked path — not throw.
    /// </summary>
    [Fact]
    public async Task NullPartySize_DoesNotThrow_DefaultsToOne_TrackedPathFires()
    {
        var (tenantId, opId, intentId) = ("t-nullps", "op-nullps", "int-nullps");
        var serviceId = "svc-nullps";
        var startsAt = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        var slotId = $"{serviceId}|{startsAt:O}";
        var holdId = "hold_nullps";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);

        // Mirror the real BookingRequest serialization: explicit nulls for absent fields.
        var rev = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload =
                $"{{\"operation\":\"create\",\"service_id\":\"{serviceId}\",\"location_id\":\"\"," +
                $"\"starts_at\":\"{startsAt:O}\",\"party_size\":null,\"duration_minutes\":null," +
                "\"target_booking_id\":\"\",\"target_booking_version\":null,\"special_requests\":[]}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        };
        var hold = MakeHold(tenantId, holdId, slotId);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);
        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = serviceId,
                StartsAt = startsAt,
                TotalCapacity = 2,
                Version = 0,
            });
        _capacityReservation.Setup(r => r.ReserveAsync(
                It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.Held(hold));
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        // Tracked path fired with capacity defaulted to 1 (party_size was null).
        _capacityReservation.Verify(r => r.ReserveAsync(
            It.Is<ReserveRequest>(req => req.SlotId == slotId && req.RequestedCapacity == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// FIX 1 back-compat: when starts_at is absent but requested_at is present, the handler
    /// falls back to requested_at and still derives a valid slot key (backward-compatible behavior).
    /// </summary>
    [Fact]
    public async Task RequestedAt_FallbackWhenStartsAtAbsent_TrackedPathFires()
    {
        var (tenantId, opId, intentId) = ("t-fallback-f", "op-fallback-f", "int-fallback-f");
        var resourceId = "r6";
        var requestedAt = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{requestedAt:O}";
        var holdId = "hold_fallback_test";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);

        // Revision uses requested_at only (back-compat payload, no starts_at).
        var rev = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = JsonSerializer.Serialize(new
            {
                party_size = 1,
                resource_id = resourceId,
                requested_at = requestedAt.ToString("O"),
                // NO starts_at
            }),
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        };
        var hold = MakeHold(tenantId, holdId, slotId);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);

        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rev);

        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = requestedAt,
                TotalCapacity = 2,
                Version = 0,
            });

        _capacityReservation.Setup(r => r.ReserveAsync(
                It.Is<ReserveRequest>(req => req.SlotId == slotId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.Held(hold));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Back-compat fallback fires: ReserveAsync was called with the requested_at-derived slotId.
        Assert.Equal(StepOutcome.Continue, result);

        _capacityReservation.Verify(r => r.ReserveAsync(
            It.Is<ReserveRequest>(req => req.SlotId == slotId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FIX 7: HoldTtl default is 45 minutes ──────────────────────────────────────────────────

    /// <summary>
    /// FIX 7: CapacityOptions.HoldTtl default is 45 minutes (deposit window 30m + 15m buffer).
    /// </summary>
    [Fact]
    public void CapacityOptions_DefaultHoldTtl_Is45Minutes()
    {
        var options = new CapacityOptions();
        Assert.Equal(TimeSpan.FromMinutes(45), options.HoldTtl);
    }
    // ── The untracked path must not be silent ───────────────────────────────────────────────

    /// <summary>
    /// The untracked branch is correct behaviour for an un-provisioned resource — and it was also
    /// completely silent. `capacity_slots` had a repository read and no writer, so the table was
    /// empty in every deployment and this branch took 100% of traffic: **no booking was ever
    /// capacity-checked**, and nothing in logs, metrics or audit said so. It was discoverable only
    /// by reading the handler.
    ///
    /// The behaviour is deliberately unchanged. What is pinned here is that the platform now says
    /// what it is doing, and says WHICH of the two untracked cases it hit — they need different
    /// fixes: `slot_not_declared` means the venue has not declared that slot, `no_slot_key` means
    /// the payload had no resource/time dimension to key on at all.
    /// </summary>
    [Fact]
    public async Task NotTrackedSlot_CountsTheUncheckedBooking_WithTheReasonItWasUnchecked()
    {
        var (tenantId, opId, intentId) = ("t-untracked-m", "op-untracked-m", "int-untracked-m");
        var resourceId = "r9";
        var requestedAt = new DateTimeOffset(2026, 7, 3, 14, 0, 0, TimeSpan.Zero);
        var slotId = $"{resourceId}|{requestedAt:O}";

        var op = MakeOp(tenantId, opId, OperationStatus.ReservingCapacity, version: 0, intentId: intentId);

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(op);
        _intentRepo.Setup(r => r.GetRevisionAsync(tenantId, intentId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRevision(tenantId, intentId, resourceId, requestedAt, partySize: 1));
        _slotRepo.Setup(r => r.GetAsync(tenantId, slotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapacitySlot?)null);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        using var metrics = new Metrics();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        long counted = 0;
        string? reason = null;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == metrics.Meter
                && instrument.Name == Cordia.Domain.Observability.MetricNames.FulfillmentCapacityUntrackedTotal)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            counted += measurement;
            foreach (var tag in tags)
            {
                if (tag.Key == Cordia.Domain.Observability.MetricNames.Tags.Reason)
                    reason = tag.Value as string;
            }
        });
        listener.Start();

        await CreateHandler(metrics: metrics).Handle(new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(1, counted);
        Assert.Equal("slot_not_declared", reason);

        // The behaviour itself must NOT have changed — this is a signal, not a new gate.
        _capacityReservation.Verify(r => r.ReserveAsync(
            It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
