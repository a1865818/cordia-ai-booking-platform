using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.ModifyBooking;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Modify/reschedule fulfilment (spec 06 §11, spec 08 §9.4.8).
///
/// Before this handler existed the engine could only create and cancel, and a modify reaching
/// Finalizing was refused outright — because routing it to the create path would have put a
/// SECOND booking into the venue's system while the original stayed pending, and reported
/// `booked` for it. The first test below is the one that matters: the provider is AMENDED, never
/// re-created.
/// </summary>
[Trait("Category", "Unit")]
public class ModifyBookingTests
{
    private const string TenantId = "t-mod";
    private const string OperationId = "op-mod";
    private const string BookingId = "bk-mod";
    private const string ProviderRef = "prov-1";

    private readonly Mock<IBookingOperationRepository> _ops = new();
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IBookingIntentRepository> _intents = new();
    private readonly Mock<IWorkflowStepRepository> _steps = new();
    private readonly Mock<IBookingProviderAdapter> _adapter = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private readonly List<OutboxMessage> _emitted = [];
    private OperationStatus? _settledTo;
    private string? _settledReason;
    private BookingLifecycleStatus? _lifecycleSetTo;

    public ModifyBookingTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> work, CancellationToken ct) => work(ct));

        _steps.Setup(s => s.GetByOperationAndTypeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        // Create-or-load is the repository's job now (GetOrAddAsync, ON CONFLICT DO NOTHING), so
        // the default is "nobody raced us": the candidate comes straight back.
        _steps.Setup(s => s.GetOrAddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep candidate, CancellationToken _) => candidate);
        _steps.Setup(s => s.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<WorkflowStepStatus>(), It.IsAny<WorkflowStepStatus>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _outbox.Setup(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((m, _) => _emitted.Add(m))
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _ops.Setup(o => o.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, long, OperationStatus, OperationStatus, string?, string?, CancellationToken>(
                (_, _, _, _, next, reason, _, _) => { _settledTo = next; _settledReason = reason; })
            .ReturnsAsync(1);

        _bookings.Setup(b => b.TryTransitionLifecycleAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, long, BookingLifecycleStatus, DateTimeOffset, CancellationToken>(
                (_, _, _, next, _, _) => _lifecycleSetTo = next)
            .ReturnsAsync(1);

        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload = """{"operation":"modify","starts_at":"2026-09-02T09:00:00.000Z"}""",
                PayloadHash = "h2",
            });
    }

    private readonly Mock<ICapacityReservation> _capacity = new();

    private readonly Mock<ICapacityHoldRepository> _holds = new();

    private readonly Mock<ICapacityLockOrder> _lockOrder = new();

    private ModifyBookingCommandHandler Build() => new(
        _ops.Object, _bookings.Object, _intents.Object, _steps.Object, _adapter.Object,
        _outbox.Object, _audit.Object, _uow.Object, _capacity.Object, _holds.Object,
        _lockOrder.Object, new Metrics());

    /// <summary>
    /// Capacity ownership TRANSFERS to the reschedule: the old slot's finalized hold is released
    /// and this operation's own hold is finalized, both inside the settlement transaction.
    ///
    /// <para>Neither happened before. `FinalizeAgainstOperationAsync` is the only Active→Finalized
    /// transition and lives in <c>FinalizeBookingCommandHandler</c>, which this lane deliberately
    /// bypasses — so the old slot stayed booked forever for a booking that had moved away, and
    /// this operation's hold on the NEW slot sat Active until the expiry sweeper released it,
    /// after which the slot read free while a confirmed booking occupied it. It also broke cancel:
    /// the newest Booked op for the booking is this one, and releasing against an operation with
    /// no finalized hold silently released nothing.</para>
    /// </summary>
    [Fact]
    public async Task Modify_TransfersCapacityFromThePreviousOwnerToThisOperation()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _ops.Setup(o => o.GetBookedOperationForBookingAsync(
                TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = "op_create_original",
                BookingId = BookingId,
                Status = OperationStatus.Booked,
                OperationType = OperationTypeEnum.Create,
            });

        await RunAsync();

        // The seat the booking is leaving is given up...
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            TenantId, "op_create_original", ReleaseReason.Superseded, It.IsAny<CancellationToken>()), Times.Once);
        // ...and the seat it is moving to is settled under THIS operation, so a later cancel —
        // which resolves the newest Booked op — finds a finalized hold to release.
        _capacity.Verify(c => c.FinalizeAgainstOperationAsync(
            TenantId, OperationId, "create", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A first reschedule whose previous owner resolves to this same operation must not release
    /// the hold it is about to finalize.
    /// </summary>
    [Fact]
    public async Task Modify_DoesNotReleaseItsOwnHoldWhenItIsAlreadyTheBookedOperation()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _ops.Setup(o => o.GetBookedOperationForBookingAsync(
                TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = OperationId,
                BookingId = BookingId,
                Status = OperationStatus.Booked,
                OperationType = OperationTypeEnum.Reschedule,
            });

        await RunAsync();

        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _capacity.Verify(c => c.FinalizeAgainstOperationAsync(
            TenantId, OperationId, "create", It.IsAny<CancellationToken>()), Times.Once);
    }

    private void GivenOperation(
        OperationStatus status = OperationStatus.Finalizing,
        OperationTypeEnum type = OperationTypeEnum.Modify,
        string? bookingId = BookingId)
        => _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = OperationId,
                IntentId = "int-1",
                IntentRevision = 2,
                OperationType = type,
                Status = status,
                BookingId = bookingId,
                Version = 3,
            });

    private void GivenBooking(BookingLifecycleStatus lifecycle = BookingLifecycleStatus.Confirmed,
        string? providerRef = ProviderRef)
        => _bookings.Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                BookingReference = "CORD-1",
                ProviderReference = providerRef,
                LifecycleStatus = lifecycle,
                Version = 1,
            });

    private void GivenProvider(ProviderResultKind kind)
        => _adapter.Setup(a => a.ModifyBookingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(kind, ProviderRef));

    private Task<StepOutcome> RunAsync()
        => Build().Handle(new ModifyBookingCommand(TenantId, OperationId), CancellationToken.None);

    // -------------------------------------------------------------------------

    [Fact]
    public async Task AmendsTheExistingBookingAndNeverCreatesASecondOne()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        await RunAsync();

        _adapter.Verify(a => a.ModifyBookingAsync(
            It.IsAny<string>(), ProviderRef, It.IsAny<ProviderBookingRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        // The entire reason this handler exists. A create here leaves the original booking
        // pending and puts a disconnected duplicate into the venue's system.
        _adapter.Verify(a => a.CreateBookingAsync(
            It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(OperationStatus.Booked, _settledTo);
        Assert.Equal("modified", _settledReason);
    }

    [Fact]
    public async Task SendsTheNewRevisionsPayloadToTheProvider()
    {
        GivenOperation();
        GivenBooking();
        ProviderBookingRequest? sent = null;
        _adapter.Setup(a => a.ModifyBookingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ProviderBookingRequest, CancellationToken>((_, _, r, _) => sent = r)
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created, ProviderRef));

        await RunAsync();

        // The amendment must carry the NEW revision, not the original booking's payload.
        Assert.Contains("2026-09-02", sent!.CanonicalPayloadJson);
        // Stable key so a redelivery cannot apply the amendment twice.
        Assert.Equal(OperationId, sent.ProviderIdempotencyKey);
    }

    [Fact]
    public async Task AnnouncesTheModificationOnlyWithTheOperationSettlement()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        await RunAsync();

        // Inv-8: the guest cannot be told "moved" before it is true — same transaction.
        var message = Assert.Single(_emitted);
        Assert.Equal(ModifyBookingCommandHandler.ModifyConfirmedEventType, message.MessageType);
    }

    [Theory]
    [InlineData(ProviderResultKind.Ambiguous)]
    [InlineData(ProviderResultKind.Transient)]
    [InlineData(ProviderResultKind.PermanentError)]
    public async Task AnUncertainProviderOutcomeReconcilesRatherThanReporting(ProviderResultKind kind)
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(kind);

        await RunAsync();

        // Inv-7: we do not know whether the amendment applied, so we must not claim it did — and
        // must not blindly retry it either.
        Assert.Equal(OperationStatus.Reconciling, _settledTo);
        Assert.Empty(_emitted);
    }

    /// <summary>
    /// A provider that says it has no such booking has NOT moved one, so the operation must not
    /// report that it did.
    ///
    /// <para>NotFound used to settle as <c>Booked</c> with reason <c>modified_no_provider_effect</c>
    /// — reasoning borrowed from the create lane, where NotFound genuinely means "nothing was
    /// made". For a modify it means the venue has no record of a booking this platform holds a
    /// reference to, and settling it finalized the new slot's capacity, released the old one,
    /// cleared ModificationPending and told the guest the booking had moved. Four claims about a
    /// reservation the venue is still holding at the original time.</para>
    ///
    /// <para>It is not retryable either — the booking will not reappear — so it takes the ambiguous
    /// route: Reconciling, which escalates modify/reschedule to a human.</para>
    /// </summary>
    [Fact]
    public async Task AProviderThatNeverHeldTheBookingReconcilesRatherThanClaimingAMove()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.NotFound);

        await RunAsync();

        Assert.Equal(OperationStatus.Reconciling, _settledTo);
        Assert.Equal("modify_notfound", _settledReason);
        // Nothing is announced and no capacity moves while the two systems disagree.
        Assert.Empty(_emitted);
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The first-party case is NOT the same and still settles: with no provider reference there was
    /// never an external reservation to disagree with, so the platform is the whole record and the
    /// amendment stands.
    /// </summary>
    [Fact]
    public async Task AFirstPartyBookingWithNoProviderReferenceStillSettles()
    {
        GivenOperation();
        GivenBooking(providerRef: null);

        await RunAsync();

        Assert.Equal(OperationStatus.Booked, _settledTo);
        Assert.Equal("modified_no_provider_effect", _settledReason);
    }

    /// <summary>
    /// A cancelled target settles through a §11-LEGAL edge, and one that gives the seat back.
    ///
    /// <para>It used to write Finalizing→Rejected, which is not in
    /// <c>OperationStateMachine</c>'s edge set at all — <c>TryTransitionStatusAsync</c> guards
    /// status and version, never legality, so the illegal edge simply succeeded and the state
    /// machine stopped describing the engine. Worse, this operation is holding an Active hold on
    /// the slot it was moving TO (every non-Cancel type reserves before Finalizing) and a terminal
    /// Rejected has no path that releases it, so the seat sat reserved until the expiry sweeper.
    /// CompensationRequired is legal, and its handler releases the hold and terminalizes.</para>
    /// </summary>
    [Fact]
    public async Task ACancelledBookingSettlesThroughCompensationRatherThanAnIllegalEdge()
    {
        GivenOperation();
        GivenBooking(BookingLifecycleStatus.Cancelled);

        await RunAsync();

        // A business outcome, not a transient failure: no retry can make a cancelled booking
        // movable, and the provider must never be called.
        Assert.Equal(OperationStatus.CompensationRequired, _settledTo);
        Assert.True(OperationStateMachine.IsAllowed(OperationStatus.Finalizing, _settledTo!.Value));
        _adapter.Verify(a => a.ModifyBookingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }


    [Fact]
    public async Task RefusesAModifyWithNoTargetRatherThanLooping()
    {
        GivenOperation(bookingId: null);

        // Retrying can never discover a target; poison it so it is quarantined (Inv-14).
        await Assert.ThrowsAsync<PoisonMessageException>(RunAsync);
    }

    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Booked)]
    public async Task DoesNothingUnlessTheOperationIsFinalizing(OperationStatus status)
    {
        GivenOperation(status);

        Assert.Equal(StepOutcome.Continue, await RunAsync());
        _adapter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LeavesACreateOperationAlone()
    {
        GivenOperation(type: OperationTypeEnum.Create);

        Assert.Equal(StepOutcome.Continue, await RunAsync());
        _adapter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandlesRescheduleIdenticallyToModify()
    {
        GivenOperation(type: OperationTypeEnum.Reschedule);
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        await RunAsync();

        Assert.Equal(OperationStatus.Booked, _settledTo);
    }

    [Fact]
    public async Task DoesNotActOnAnotherTenantsOperation()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = "other-tenant",
                OperationId = OperationId,
                IntentId = "int-1",
                IntentRevision = 2,
                OperationType = OperationTypeEnum.Modify,
                Status = OperationStatus.Finalizing,
                BookingId = BookingId,
            });

        Assert.Equal(StepOutcome.Wait, await RunAsync());
        _adapter.VerifyNoOtherCalls();
    }
    // -------------------------------------------------------------------------
    // The modify lane must close the pending state its own front door opened.
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /api/Bookings/{id}/modifications</c> parks the booking at
    /// <c>ModificationPending</c> so concurrent writers can see a change in flight. It is the only
    /// writer of that status, and nothing cleared it — so a successfully amended booking read as
    /// "modification pending" forever while the operation said <c>booked</c>. Cancel has always
    /// closed its own pending state; this is the same discipline.
    /// </summary>
    [Fact]
    public async Task ClearsTheModificationPendingStateItWasHandedOnSuccess()
    {
        GivenOperation();
        GivenBooking(lifecycle: BookingLifecycleStatus.ModificationPending);
        GivenProvider(ProviderResultKind.Created);

        await RunAsync();

        Assert.Equal(OperationStatus.Booked, _settledTo);
        Assert.Equal(BookingLifecycleStatus.Confirmed, _lifecycleSetTo);
    }

    /// <summary>
    /// The reverse: a booking that was never parked must not be touched. A modification submitted
    /// through the acceptance door leaves the booking Confirmed throughout, and a write here would
    /// bump its version for no reason — inviting a spurious stale-version conflict on the next
    /// concurrent change.
    /// </summary>
    [Fact]
    public async Task LeavesAConfirmedBookingsLifecycleAlone()
    {
        GivenOperation();
        GivenBooking(lifecycle: BookingLifecycleStatus.Confirmed);
        GivenProvider(ProviderResultKind.Created);

        await RunAsync();

        Assert.Equal(OperationStatus.Booked, _settledTo);
        Assert.Null(_lifecycleSetTo);
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Lock ordering: the previous owner's hold is locked BEFORE the slot is touched ────────────

    private const string SameSlotService = "svc-lock-order";
    private static readonly DateTimeOffset SameSlotStart =
        DateTimeOffset.Parse("2026-09-02T09:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A same-slot amendment: the payload carries a resource dimension so a slot key derives, and
    /// the previous owner holds a FINALIZED hold on that very slot.
    /// </summary>
    private string GivenSameSlotAmendment(string previousOperationId)
    {
        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload =
                    $$"""{"operation":"modify","service_id":"{{SameSlotService}}","starts_at":"2026-09-02T09:00:00.000Z","party_size":4}""",
                PayloadHash = "h2",
            });

        // Derived with the production helper, so the test cannot drift from the key the handler
        // actually computes.
        var slotId = Cordia.Domain.Capacity.CapacitySlotKey.Derive(
            null, SameSlotService, null, SameSlotStart)!;

        _ops.Setup(o => o.GetBookedOperationForBookingAsync(
                TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = previousOperationId,
                BookingId = BookingId,
                Status = OperationStatus.Booked,
                OperationType = OperationTypeEnum.Create,
            });

        _holds.Setup(h => h.GetLatestByOperationAndStepAsync(
                TenantId, previousOperationId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacityHold
            {
                TenantId = TenantId,
                HoldId = "hold_previous_owner",
                OperationId = previousOperationId,
                WorkflowStep = "create",
                SlotId = slotId,
                ReservedCapacity = 2,
                Status = HoldStatus.Finalized,
            });

        return slotId;
    }

    /// <summary>
    /// Every capacity row this transaction will touch is acquired BEFORE anything writes one.
    ///
    /// <para>`FinalizeAgainstOperationAsync` takes this operation's own hold and then the SLOT;
    /// `AbsorbSameSlotAsync` afterwards wants the PREVIOUS owner's hold. A concurrent cancel of the
    /// same booking takes those two rows the other way round (hold, then slot), so the two
    /// transactions could each hold one and wait on the other — Postgres aborts one with 40P01.
    /// Nothing is corrupted and redelivery re-runs the loser, but a whole transaction's work is
    /// discarded and the pair can keep colliding.</para>
    ///
    /// <para>Asserted as an ORDER rather than a count: the acquisition existing but landing after
    /// the finalize would restore the exact cycle it is meant to remove.</para>
    /// </summary>
    [Fact]
    public async Task SameSlotModify_AcquiresEveryCapacityRowBeforeTouchingTheSlot()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);
        GivenSameSlotAmendment("op_create_original");

        var calls = new List<string>();

        _lockOrder.Setup(l => l.AcquireAsync(
                TenantId,
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("acquire"))
            .Returns(Task.CompletedTask);

        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                TenantId, OperationId, "create", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("finalize"))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.Finalized);

        _capacity.Setup(c => c.AbsorbSameSlotAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("absorb"))
            .ReturnsAsync(true);

        await RunAsync();

        Assert.Equal(["acquire", "finalize", "absorb"], calls);
    }

    /// <summary>
    /// The acquisition names the previous owner's hold AND both slot rows — not just the hold.
    ///
    /// <para>Locking holds before slots removes the hold↔slot cycle and nothing else. A same-slot
    /// amendment resolves both slot ids to the same row, so passing them is redundant here; the
    /// point of pinning it on this path too is that the two lanes build the SAME acquisition set,
    /// which is what stops the cross-slot branch below from being the only one that gets it right.</para>
    /// </summary>
    [Fact]
    public async Task SameSlotModify_AcquisitionNamesThePreviousHoldAndTheSlot()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);
        var slotId = GivenSameSlotAmendment("op_create_original");

        IEnumerable<string?>? holdIds = null;
        IEnumerable<string?>? slotIds = null;

        _lockOrder.Setup(l => l.AcquireAsync(
                TenantId,
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string?>, IEnumerable<string?>, CancellationToken>(
                (_, h, s, _) => { holdIds = h.ToList(); slotIds = s.ToList(); })
            .Returns(Task.CompletedTask);

        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                TenantId, OperationId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.Finalized);

        _capacity.Setup(c => c.AbsorbSameSlotAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await RunAsync();

        Assert.NotNull(holdIds);
        Assert.Contains("hold_previous_owner", holdIds!);
        Assert.NotNull(slotIds);
        Assert.Contains(slotId, slotIds!);
    }

    /// <summary>
    /// A genuine RESCHEDULE acquires BOTH slot rows up front — the case the previous fix missed.
    ///
    /// <para>This path used to take no lock at all, on the argument that a cross-slot move and a
    /// cancel "never contend" because the new slot and the old slot are different rows. That is
    /// true of a reschedule versus a cancel. It is false of a reschedule versus ANOTHER
    /// reschedule: one moving <c>S1 → S2</c> takes the new slot then the old, and one moving
    /// <c>S2 → S1</c> takes those same two rows in the opposite order. Each holds one, each wants
    /// the other, and the cycle is built entirely out of slot rows — so ordering holds before slots
    /// does not touch it. Two guests swapping between the same two sittings is enough.</para>
    ///
    /// <para>Naming both slots in one acquisition lets <c>ICapacityLockOrder</c> sort them, which
    /// is what makes the pair take them in the same direction.</para>
    /// </summary>
    [Fact]
    public async Task CrossSlotReschedule_AcquiresBothSlotsBeforeTouchingEither()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        // The TARGET slot must actually derive, or the handler has only one slot id to name and the
        // cross-slot shape is not under test at all.
        var targetSlotId = GivenSameSlotAmendment("op_create_original");

        // …but the previous owner sits on a DIFFERENT slot. That is the genuine reschedule: two
        // distinct rows, which is exactly the pair two opposing reschedules can cycle on.
        _holds.Setup(h => h.GetLatestByOperationAndStepAsync(
                TenantId, "op_create_original", "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacityHold
            {
                TenantId = TenantId,
                HoldId = "hold_previous_owner",
                OperationId = "op_create_original",
                WorkflowStep = "create",
                SlotId = "slot_old",
                ReservedCapacity = 2,
                Status = HoldStatus.Finalized,
            });

        Assert.NotEqual("slot_old", targetSlotId);

        var calls = new List<string>();
        IEnumerable<string?>? slotIds = null;

        _lockOrder.Setup(l => l.AcquireAsync(
                TenantId,
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string?>, IEnumerable<string?>, CancellationToken>(
                (_, _, s, _) => { slotIds = s.ToList(); calls.Add("acquire"); })
            .Returns(Task.CompletedTask);

        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                TenantId, OperationId, "create", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("finalize"))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.Finalized);

        await RunAsync();

        // Acquired first, and BOTH slots named — the old one it will release and the new one it
        // will finalize into.
        Assert.Equal("acquire", calls[0]);
        Assert.NotNull(slotIds);
        Assert.Contains("slot_old", slotIds!);
        Assert.Contains(targetSlotId, slotIds!);
        Assert.Equal(2, slotIds!.Where(s => !string.IsNullOrEmpty(s)).Distinct().Count());
    }

    // ── Authorization material does not cross to the provider ───────────────────────────────────

    /// <summary>
    /// A spec-08 verification grant must not be shipped to a third-party booking provider.
    ///
    /// <para>The canonical payload of an operation accepted through
    /// <c>POST /api/Bookings/{id}/modifications</c> is a serialized <c>SubmitModificationInput</c>,
    /// which carries <c>grant_id</c>; <c>confirmation.identity</c> holds a <c>grant_</c>-prefixed
    /// id on the agent lane. Both are the credential that authorizes reading and mutating the
    /// booking (<c>X-Grant-Id</c>), and both were passed to the provider verbatim on every modify,
    /// landing in that provider's request logs.</para>
    /// </summary>
    [Fact]
    public async Task Modify_DoesNotSendTheVerificationGrantToTheProvider()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload =
                    """{"target_booking_id":"bk-mod","grant_id":"grant_secret","confirmation":{"identity":"grant_secret","confirmation_id":"c1"},"expected_booking_version":3}""",
                PayloadHash = "h2",
            });

        ProviderBookingRequest? sent = null;
        _adapter.Setup(a => a.ModifyBookingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, ProviderBookingRequest, CancellationToken>(
                (_, _, req, _) => sent = req)
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created, ProviderRef));

        await RunAsync();

        Assert.NotNull(sent);
        Assert.DoesNotContain("grant_secret", sent!.CanonicalPayloadJson);
        // The rest of the payload still travels — this strips credentials, it does not gut the body.
        Assert.Contains("bk-mod", sent.CanonicalPayloadJson);
        Assert.Contains("c1", sent.CanonicalPayloadJson);
    }

    /// <summary>
    /// The strip must not FALL BACK to the unredacted payload when it cannot read one field.
    ///
    /// <para><c>confirmation.identity</c> was read with <c>GetValue&lt;string&gt;()</c>, which
    /// throws on a non-string node. The throw landed in a catch that returned the ORIGINAL
    /// payload — but <c>grant_id</c> had already been removed from the copy, so a partially
    /// applied strip was discarded and the live grant shipped to the provider anyway. A payload
    /// whose <c>identity</c> is a number, an object or null is unusual, not impossible, and it
    /// defeated the control completely rather than degrading it.</para>
    ///
    /// <para>Theory rather than one case, because each JSON kind took the same broken path and
    /// any of them alone would have kept the bug alive.</para>
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"kind\":\"staff\"}")]
    [InlineData("[\"a\"]")]
    [InlineData("true")]
    public async Task Modify_StillStripsTheGrant_WhenConfirmationIdentityIsNotAString(string identityJson)
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload =
                    $$"""{"target_booking_id":"bk-mod","grant_id":"grant_secret","confirmation":{"identity":{{identityJson}},"confirmation_id":"c1"},"expected_booking_version":3}""",
                PayloadHash = "h2",
            });

        ProviderBookingRequest? sent = null;
        _adapter.Setup(a => a.ModifyBookingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, ProviderBookingRequest, CancellationToken>(
                (_, _, req, _) => sent = req)
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created, ProviderRef));

        await RunAsync();

        Assert.NotNull(sent);
        Assert.DoesNotContain("grant_secret", sent!.CanonicalPayloadJson);
        // Still a modify, not a gutted one: the non-string identity is left exactly as it was,
        // because it is not a grant — only the credential is removed.
        Assert.Contains("bk-mod", sent.CanonicalPayloadJson);
        Assert.Contains("c1", sent.CanonicalPayloadJson);
    }

    /// <summary>An unparseable payload is passed through untouched — a strip must never fail a modify.</summary>
    [Fact]
    public async Task Modify_PassesAnUnparseablePayloadThrough_RatherThanFailing()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload = "{not json at all",
                PayloadHash = "h2",
            });

        ProviderBookingRequest? sent = null;
        _adapter.Setup(a => a.ModifyBookingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProviderBookingRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, ProviderBookingRequest, CancellationToken>(
                (_, _, req, _) => sent = req)
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created, ProviderRef));

        await RunAsync();

        Assert.NotNull(sent);
        Assert.Equal("{not json at all", sent!.CanonicalPayloadJson);
    }

    // ── A seat is handed on only when it is known where it WENT ──────────────────────────────────

    /// <summary>
    /// A modification payload derives NO slot, and that must not be read as "a different slot".
    ///
    /// <para><c>POST /api/Bookings/{id}/modifications</c> stores <c>SubmitModificationInput</c> as
    /// its canonical payload; that type carries no <c>starts_at</c>, no resource dimension and no
    /// <c>party_size</c>, so every such operation derives <c>(1, null)</c>. Guarded only on
    /// "not the same slot", the settlement then released the previous owner's FINALIZED hold as
    /// <c>Superseded</c> while this operation had reserved nothing of its own — the seats of a
    /// confirmed booking went back on sale with the guest still at the table.</para>
    /// </summary>
    [Fact]
    public async Task Modify_DoesNotReleaseATrackedSeat_WhenThePayloadNamesNoTargetSlot()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload =
                    """{"target_booking_id":"bk-mod","expected_booking_version":3,"grant_id":"g1"}""",
                PayloadHash = "h2",
            });

        _ops.Setup(o => o.GetBookedOperationForBookingAsync(
                TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = "op_create_original",
                BookingId = BookingId,
                Status = OperationStatus.Booked,
                OperationType = OperationTypeEnum.Create,
            });

        // The previous owner is sitting on a REAL first-party slot — there is a seat to lose.
        _holds.Setup(h => h.GetLatestByOperationAndStepAsync(
                TenantId, "op_create_original", "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacityHold
            {
                TenantId = TenantId,
                HoldId = "hold_previous_owner",
                OperationId = "op_create_original",
                WorkflowStep = "create",
                SlotId = "slot_old",
                ReservedCapacity = 4,
                Status = HoldStatus.Finalized,
            });

        await RunAsync();

        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
                TenantId, "op_create_original", ReleaseReason.Superseded, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A same-slot amendment that does not restate <c>party_size</c> does not change it.
    ///
    /// <para><c>AbsorbSameSlotAsync</c> takes its size argument as the AUTHORITATIVE total, and
    /// <c>party_size</c> is optional in the shared schema — absent, explicit null and the TriState
    /// string <c>"unknown"</c> all derive the fallback of 1. Passed through, a party of four was
    /// resized to one and three seats it still occupied became sellable.</para>
    /// </summary>
    [Fact]
    public async Task SameSlotModify_KeepsTheCurrentPartySize_WhenThePayloadDoesNotRestateIt()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.Created);

        var slotId = GivenSameSlotAmendment("op_create_original");

        // Same slot, same booking — but the amendment says nothing about the party size.
        _intents.Setup(i => i.GetRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = TenantId,
                IntentId = "int-1",
                Revision = 2,
                CanonicalPayload =
                    $$"""{"operation":"modify","service_id":"{{SameSlotService}}","starts_at":"2026-09-02T09:00:00.000Z","party_size":"unknown","special_requests":"window seat"}""",
                PayloadHash = "h2",
            });

        int? absorbedTo = null;

        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                TenantId, OperationId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.Finalized);

        _capacity.Setup(c => c.AbsorbSameSlotAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, int, CancellationToken>(
                (_, _, _, _, _, capacity, _) => absorbedTo = capacity)
            .ReturnsAsync(true);

        await RunAsync();

        Assert.NotNull(slotId);
        // The previous owner's finalized hold is for 2 (see GivenSameSlotAmendment) — the booking
        // keeps the size it has. It was resized to the defaulted 1 before this.
        Assert.Equal(2, absorbedTo);
    }

    /// <summary>
    /// A modify may not settle as Booked while a CANCELLATION is in flight.
    ///
    /// <para>The in-transaction guard rejected only `Cancelled`, so a booking parked at
    /// `CancellationPending` by `POST .../cancellations` sailed through: the modify finalized its
    /// hold, settled capacity, committed `Booked` and emitted the `modified` notification — telling
    /// the guest their booking had moved while another operation was actively removing it.</para>
    ///
    /// <para>Same disposition as `Cancelled`: reconcile, and let a human establish which of the two
    /// actually happened.</para>
    /// </summary>
    [Fact]
    public async Task Modify_RoutesToReconciling_WhenACancellationIsInFlight()
    {
        GivenOperation();
        GivenProvider(ProviderResultKind.Created);

        // Live when the handler starts...
        _bookings.SetupSequence(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                BookingReference = "CORD-1",
                ProviderReference = ProviderRef,
                LifecycleStatus = BookingLifecycleStatus.Confirmed,
                Version = 1,
            })
            // ...and parked for cancellation by the time the settlement transaction re-reads it.
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                BookingReference = "CORD-1",
                ProviderReference = ProviderRef,
                LifecycleStatus = BookingLifecycleStatus.CancellationPending,
                Version = 2,
            });

        await RunAsync();

        Assert.Equal(OperationStatus.Reconciling, _settledTo);
        Assert.Equal("modify_target_cancellation_in_flight", _settledReason);

        // Nothing may tell the guest the booking moved.
        Assert.Empty(_emitted);
    }
}
