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
        _steps.Setup(s => s.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
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

    private ModifyBookingCommandHandler Build() => new(
        _ops.Object, _bookings.Object, _intents.Object, _steps.Object, _adapter.Object,
        _outbox.Object, _audit.Object, _uow.Object, _capacity.Object, new Metrics());

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

    [Fact]
    public async Task AProviderThatNeverHeldTheBookingStillSettlesWithoutClaimingAnEffect()
    {
        GivenOperation();
        GivenBooking();
        GivenProvider(ProviderResultKind.NotFound);

        await RunAsync();

        Assert.Equal(OperationStatus.Booked, _settledTo);
        // The reason has to say no provider effect occurred, or the audit trail claims one did.
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

}
