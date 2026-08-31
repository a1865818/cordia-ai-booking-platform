using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ResolveManualReviewCommandHandler"/>.
/// Verifies: Booked (requires ProviderRef) → Booked + notification.booking_confirmed.v1 + work item closed;
/// FailedFinal → FailedFinal + notification.booking_failed.v1 + work item closed;
/// Booked without ProviderRef → OperationConflictException;
/// non-ManualReview → OperationConflictException; not-found → OperationConflictException.
/// </summary>
[Trait("Category", "Unit")]
public class ResolveManualReviewCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ResolveManualReviewCommandHandlerTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // The uncontended lifecycle transition SUCCEEDS — Moq's default of 0 means "a concurrent
        // writer moved the booking", which the handler now treats as a refusal rather than
        // ignoring. Tests that want the lost race override this explicitly.
        _bookingRepo.Setup(r => r.TryTransitionLifecycleAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Same for the ownership-guarded release. Zero rows there means the booking moved OR changed
        // hands, and the Booked path treats both as the conflict it must not resolve through.
        _bookingRepo.Setup(r => r.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    private readonly Mock<IBookingRepository> _bookingRepo = new();
    private readonly Mock<ICapacityReservation> _capacity = new();
    private readonly Mock<IBookingIntentRepository> _intents = new();
    private readonly Mock<ICapacityHoldRepository> _holds = new();

    private readonly Mock<ICapacityLockOrder> _lockOrder = new();

    private ResolveManualReviewCommandHandler CreateHandler() =>
        new(_opRepo.Object, _workItemRepo.Object, _outboxRepo.Object,
            _bookingRepo.Object, _intents.Object, _capacity.Object,
            new Cordia.Application.Handlers.BookingFulfillment.ModifyBooking.ModifyCapacitySettlement(
                _intents.Object, _holds.Object, _lockOrder.Object, _capacity.Object),
            new AuditWriter(_auditRepo.Object), _uow.Object);

    private static BookingOperation MakeOp(string tenantId, string operationId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
            // EXPLICIT. `OperationTypeEnum.Cancel` is the zero value, so leaving this unset made
            // every one of these tests silently exercise a cancellation while reading as a
            // generic operation — which matters now that cancel/modify/reschedule take a
            // different manual-recovery path from a create.
            OperationType = Domain.Generated.OperationTypeEnum.Create,
        };

    // ── A resolved mutation must release the booking's pending state, either way ─────────────

    /// <summary>
    /// A modify/cancel resolved as FAILED still has to un-park the booking.
    ///
    /// <para>`POST .../modifications` parks the booking at ModificationPending and
    /// `.../cancellations` at CancellationPending. An ambiguous provider outcome routes
    /// Finalizing→Reconciling, the reconciler escalates modify/cancel straight to ManualReview, and
    /// this handler is the only way out — so when the operator resolved it as failed, the operation
    /// read `failed_final` while the booking row told every reader a change was still outstanding,
    /// permanently. Failed means nothing happened, so the booking reverts to Confirmed.</para>
    /// </summary>
    /// <remarks>
    /// Reschedule is deliberately absent: it parks nothing (it rides the spec-04 acceptance door,
    /// not `POST .../modifications`), so it may retract nothing — see
    /// <see cref="Handle_Failed_Reschedule_LeavesAnIndependentModificationPendingAlone"/>.
    /// </remarks>
    [Theory]
    [InlineData(Domain.Generated.OperationTypeEnum.Modify, BookingLifecycleStatus.ModificationPending)]
    [InlineData(Domain.Generated.OperationTypeEnum.Cancel, BookingLifecycleStatus.CancellationPending)]
    public async Task Handle_FailedFinal_RevertsThePendingLifecycleOfTheBookingItWasActingOn(
        Domain.Generated.OperationTypeEnum type, BookingLifecycleStatus parked)
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 2);
        op.OperationType = type;
        op.BookingId = bookingId;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 2,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = parked,
                Version = 9,
            });

        BookingLifecycleStatus? settledTo = null;
        _bookingRepo.Setup(r => r.TryReleasePendingAsync(
                tenantId, bookingId, 9, parked, operationId,
                It.IsAny<BookingLifecycleStatus>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, long, BookingLifecycleStatus, string, BookingLifecycleStatus, DateTimeOffset, CancellationToken>(
                (_, _, _, _, _, next, _, _) => settledTo = next)
            .ReturnsAsync(1);

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.FailedFinal, null, "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        Assert.Equal(BookingLifecycleStatus.Confirmed, settledTo);

        // The booking's OWN seat is untouched — it was never given up, so the previous owner's hold
        // is not released.
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.Is<string>(id => id != operationId),
            It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // But any seat THIS operation had already taken goes back.
        //
        // This assertion replaces a blanket `Times.Never`, which encoded a premise that stopped
        // being true: `modify_capacity_unavailable` routes an operation to Reconciling AFTER its
        // hold is Finalized and units have moved held→booked, and resolving that review as Failed
        // used to leave those units owned by an operation nothing can ever find again
        // (`GetBookedOperationForBookingAsync` matches only `Booked`), so the slot under-reported
        // its availability permanently. Scoped to this operation's own id, and a no-op for the
        // routes that finalized nothing.
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            tenantId, operationId, ReleaseReason.Compensated, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A booking already Cancelled underneath us keeps that outcome — a writer that knew decided it.</summary>
    [Fact]
    public async Task Handle_FailedFinal_LeavesABookingThatIsNoLongerPendingAlone()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 2);
        op.OperationType = Domain.Generated.OperationTypeEnum.Cancel;
        op.BookingId = bookingId;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 2,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.Cancelled,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.FailedFinal, null, "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        _bookingRepo.Verify(r => r.TryTransitionLifecycleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bookingRepo.Verify(r => r.TryReleasePendingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Booked: requires ProviderRef ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Booked_RequiresProviderRef_TransitionsAndEmitsConfirmedNotification()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string replayId = "rpl_xyz";
        const string providerRef = "ext_ref_123";
        const string actor = "ops";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null); // no open work item — still should succeed

        OutboxMessage? capturedMsg = null;
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((msg, _) => capturedMsg = msg)
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        await handler.Handle(
            new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked, providerRef, replayId, actor, "rc_test", "operator"),
            CancellationToken.None);

        // notification.booking_confirmed.v1
        Assert.NotNull(capturedMsg);
        Assert.Equal("notification.booking_confirmed.v1", capturedMsg.MessageType);
        Assert.Contains(providerRef, capturedMsg.Payload);

        // audit
        Assert.NotNull(capturedAudit);
        Assert.Equal("operation.manual_resolved", capturedAudit.EventType);
        Assert.Contains(replayId, capturedAudit.Metadata);
        Assert.Contains("booked", capturedAudit.Metadata);
    }

    [Fact]
    public async Task Handle_Booked_MissingProviderRef_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked,
                    ProviderRef: null, "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Handle_Booked_EmptyProviderRef_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked,
                    ProviderRef: "   ", "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }

    // ── FailedFinal: no ProviderRef required ─────────────────────────────────────────────────

    [Fact]
    public async Task Handle_FailedFinal_TransitionsAndEmitsFailedNotification_ClosesWorkItem()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string replayId = "rpl_abc";
        const string actor = "ops";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 7);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 7,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var workItem = new OperationWorkItem
        {
            TenantId = tenantId,
            WorkItemId = "wi-1",
            OperationId = operationId,
            State = "open",
            Version = 2,
        };
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItem);
        _workItemRepo.Setup(r => r.TryUpdateStateAsync(
                tenantId, "wi-1", 2, "open", "resolved", actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        OutboxMessage? capturedMsg = null;
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((msg, _) => capturedMsg = msg)
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        await handler.Handle(
            new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                ProviderRef: null, replayId, actor, "rc_test", "operator"),
            CancellationToken.None);

        // notification.booking_failed.v1
        Assert.NotNull(capturedMsg);
        Assert.Equal("notification.booking_failed.v1", capturedMsg.MessageType);

        // audit
        Assert.NotNull(capturedAudit);
        Assert.Equal("operation.manual_resolved", capturedAudit.EventType);
        Assert.Contains(replayId, capturedAudit.Metadata);
        Assert.Contains("failedfinal", capturedAudit.Metadata);

        // work item closed
        _workItemRepo.Verify(r => r.TryUpdateStateAsync(
            tenantId, "wi-1", 2, "open", "resolved", actor, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Not-found → OperationConflictException ───────────────────────────────────────────────

    [Fact]
    public async Task Handle_OperationNotFound_ThrowsOperationConflictException()
    {
        _opRepo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand("t1", "op-x", ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }

    // ── Non-ManualReview → OperationConflictException ────────────────────────────────────────

    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.FailedFinal)]
    public async Task Handle_NonManualReview_ThrowsOperationConflictException(OperationStatus status)
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, status);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }

    // ── Concurrent change (rows==0) → OperationConflictException ─────────────────────────────

    [Fact]
    public async Task Handle_ConcurrentChange_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 3);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, It.IsAny<long>(),
                OperationStatus.ManualReview, It.IsAny<OperationStatus>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // lost race

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }

    /// <summary>
    /// Resolving a CROSS-SLOT reschedule as Booked must not free the old seat when the new one is
    /// already gone.
    ///
    /// <para>This lane is only ever reached through ManualReview, which waits for a human —
    /// routinely far longer than the hold TTL — so by resolution time the expiry sweeper has very
    /// often already released this operation's hold on the target slot. `FinalizeAgainstOperationAsync`
    /// then answers `HoldNoLongerValid`.</para>
    ///
    /// <para>The settlement used to discard that answer and release the previous owner's hold
    /// anyway, leaving a CONFIRMED booking counted against neither slot: the seat the guest
    /// actually occupies back on sale, and nothing recorded on the slot they moved to. The
    /// automatic lane refuses for exactly this reason.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Booked_DoesNotFreeTheOldSeatWhenTheNewHoldHasExpired()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify";
        const string previousOwnerId = "op_create_original";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Reschedule;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // A different operation owns the booking's current seat — the cross-slot shape.
        _opRepo.Setup(r => r.GetBookedOperationForBookingAsync(
                tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, previousOwnerId, OperationStatus.Booked));

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.ModificationPending,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        // The target slot's hold is gone — the sweeper reclaimed it while the review sat waiting.
        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                tenantId, operationId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.HoldNoLongerValid);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        // The guest keeps the seat they are actually sitting in.
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            tenantId, previousOwnerId, ReleaseReason.Superseded, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -- A resolution may not claim more than actually happened ------------------------------

    /// <summary>
    /// A CANCEL resolved as Booked must not commit success while the booking is still live.
    ///
    /// <para>The lifecycle transition was reached only when the booking was CancellationPending and
    /// its result was ignored, so outside that state the block was skipped entirely: the operation
    /// recorded Booked and the outbox announced the cancellation had succeeded while the booking
    /// stayed live - a guest told their booking was cancelled, sitting in a slot the venue still
    /// has reserved.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Booked_Cancel_RefusesWhenTheBookingCannotBeCancelled()
    {
        const string tenantId = "t1";
        const string operationId = "op-cancel";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Cancel;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.Confirmed,
                Version = 9,
            });

        // A concurrent writer moved the booking - the cancellation does not land.
        _bookingRepo.Setup(r => r.TryTransitionLifecycleAsync(
                tenantId, bookingId, 9, BookingLifecycleStatus.Cancelled,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new ResolveManualReviewCommand(
                    tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
                CancellationToken.None));

        // Nothing may claim the cancellation succeeded.
        _outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>An already-cancelled booking is idempotent, not a conflict - another writer did it.</summary>
    [Fact]
    public async Task Handle_Booked_Cancel_AcceptsABookingAlreadyCancelled()
    {
        const string tenantId = "t1";
        const string operationId = "op-cancel-2";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Cancel;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.Cancelled,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A MODIFY resolved as Booked must refuse once the booking has been cancelled underneath it.
    ///
    /// <para>A concurrent cancellation can win the booking row while the review waits for a human.
    /// The settlement went on to finalize the new hold, release the previous owner's seat, record
    /// the operation Booked and emit a CONFIRMED outcome - for a booking that is cancelled.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Booked_Modify_RefusesWhenTheBookingWasCancelledUnderneath()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify-2";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Reschedule;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.Cancelled,
                Version = 9,
            });

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new ResolveManualReviewCommand(
                    tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
                CancellationToken.None));

        // Refused BEFORE any capacity or outbox write.
        _capacity.Verify(c => c.FinalizeAgainstOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A lost race on the pending-change clear refuses, rather than settling capacity and
    /// committing Booked over a booking still claiming a change is outstanding.
    /// </summary>
    [Fact]
    public async Task Handle_Booked_Modify_RefusesWhenThePendingChangeCannotBeCleared()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify-3";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        // MODIFY, not Reschedule: `ModificationPending` is parked only by `POST .../modifications`,
        // so only a Modify may clear it — and therefore only a Modify can be refused for failing to.
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Modify;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.ModificationPending,
                Version = 9,
            });

        _bookingRepo.Setup(r => r.TryReleasePendingAsync(
                tenantId, bookingId, 9,
                BookingLifecycleStatus.ModificationPending, It.IsAny<string>(),
                BookingLifecycleStatus.Confirmed,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new ResolveManualReviewCommand(
                    tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
                CancellationToken.None));

        _capacity.Verify(c => c.FinalizeAgainstOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A failed MODIFY must not retract a cancellation someone else parked.
    ///
    /// <para>The revert used to fire on either pending state, so a failed Modify whose booking had
    /// since been moved to CancellationPending by a newer cancellation was pushed back to Confirmed
    /// - silently undoing an unrelated in-flight cancellation.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Failed_Modify_LeavesAnUnrelatedCancellationPendingAlone()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify-4";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Modify;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // A NEWER cancellation parked the booking. This failed modify must not touch it.
        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.CancellationPending,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.FailedFinal, null, "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        _bookingRepo.Verify(r => r.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                BookingLifecycleStatus.Confirmed, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// `NotTracked` is not a successful finalization, and the old seat must survive it.
    ///
    /// <para>It means NO first-party hold was ever created for this operation, so there is no
    /// destination seat at all - the same substantive position as `HoldNoLongerValid`, which the
    /// settlement already refuses on. Because a cross-slot reschedule still derives a non-null
    /// target slot key, the release fired anyway and the confirmed booking ended up counted against
    /// neither slot: the seat the guest actually occupies back on sale, nothing recorded on the slot
    /// they moved to.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Booked_DoesNotFreeTheOldSeatWhenTheNewSlotWasNeverTracked()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify-5";
        const string previousOwnerId = "op_create_original";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Reschedule;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _opRepo.Setup(r => r.GetBookedOperationForBookingAsync(
                tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, previousOwnerId, OperationStatus.Booked));

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.ModificationPending,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        // The destination payload derives a real slot key, so `movedToKnownSlot` is true...
        _intents.Setup(r => r.GetRevisionAsync(
                tenantId, "int-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = "int-1",
                Revision = 1,
                CanonicalPayload =
                    """{"operation":"reschedule","service_id":"svc-1","starts_at":"2026-09-02T09:00:00.000Z","party_size":2}""",
                PayloadHash = "h",
            });

        // ...and the PREVIOUS owner holds a real first-party seat there is something to lose.
        _holds.Setup(r => r.GetLatestByOperationAndStepAsync(
                tenantId, previousOwnerId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacityHold
            {
                TenantId = tenantId,
                HoldId = "hold_previous",
                OperationId = previousOwnerId,
                WorkflowStep = "create",
                SlotId = "slot_old",
                ReservedCapacity = 2,
                Status = HoldStatus.Finalized,
            });

        // But this operation never had a hold of its own on the destination.
        _capacity.Setup(c => c.FinalizeAgainstOperationAsync(
                tenantId, operationId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Cordia.Domain.Capacity.FinalizeOutcome.NotTracked);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        // The guest keeps the seat they are actually sitting in.
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            tenantId, previousOwnerId, ReleaseReason.Superseded, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An amendment may not be declared booked while a CANCELLATION is in flight either.
    ///
    /// <para>`CancellationPending` is what `POST .../cancellations` parks, and it is the third
    /// lifecycle state this branch can meet. Refusing only on `Cancelled` let the resolution
    /// finalize the new hold, release the previous owner's seat and emit a CONFIRMED outcome on top
    /// of a booking another operation was actively removing.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Booked_Modify_RefusesWhileACancellationIsInFlight()
    {
        const string tenantId = "t1";
        const string operationId = "op-modify-6";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        op.BookingId = bookingId;
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Reschedule;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.CancellationPending,
                Version = 9,
            });

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new ResolveManualReviewCommand(
                    tenantId, operationId, ManualReviewResolution.Booked, "ext_ref", "rpl_1", "ops", "rc", "operator"),
                CancellationToken.None));

        _capacity.Verify(c => c.FinalizeAgainstOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A failed RESCHEDULE must not clear a modification's pending state.
    ///
    /// <para>`ModificationPending` has one writer — `POST /api/Bookings/{id}/modifications` — and a
    /// reschedule does not go through it: `SubmitModificationInput` carries no field for a new
    /// time, so a reschedule rides the acceptance door and the booking stays `Confirmed`
    /// throughout. Reverting on that status therefore undid an INDEPENDENT modification's parking
    /// while that modification was still in flight.</para>
    /// </summary>
    [Fact]
    public async Task Handle_Failed_Reschedule_LeavesAnIndependentModificationPendingAlone()
    {
        const string tenantId = "t1";
        const string operationId = "op-resched-fail";
        const string bookingId = "bk-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 2);
        op.OperationType = Cordia.Domain.Generated.OperationTypeEnum.Reschedule;
        op.BookingId = bookingId;

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 2,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Parked by a DIFFERENT operation — a modification this reschedule knows nothing about.
        _bookingRepo.Setup(r => r.GetByIdAsync(tenantId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = bookingId,
                LifecycleStatus = BookingLifecycleStatus.ModificationPending,
                Version = 9,
            });

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(
                tenantId, operationId, ManualReviewResolution.FailedFinal, null, "rpl_1", "ops", "rc", "operator"),
            CancellationToken.None);

        _bookingRepo.Verify(r => r.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                BookingLifecycleStatus.Confirmed, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A CREATE resolved as Booked must leave a `bookings` row behind.
    ///
    /// <para>This handler was the fourth writer of the Booked outcome and the only one still
    /// producing an operation with no booking behind it: `MaterializeBookedAsync` had two call
    /// sites, both in `FinalizeBookingCommandHandler`, and reconcile gained a third while this
    /// did not. The operation, the confirmed outcome and the guest notification all committed for
    /// a booking `GetBookingDetailsQuery`, the console, and both mutation doors cannot see.</para>
    ///
    /// <para>It stopped being latent when reconcile gained the `reconciled_hold_no_longer_valid`
    /// escalation: that route makes this handler the ONLY way such an operation can reach Booked,
    /// so the gap would have been reachable through a path the same wave introduced.</para>
    /// </summary>
    [Fact]
    public async Task BookedCreate_MaterialisesTheBookingRow()
    {
        var (tenantId, operationId) = ("t-mrv-mat", "op-mrv-materialise");
        const string providerRef = "ext_ref_manual";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 2));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, It.IsAny<long>(),
                OperationStatus.ManualReview, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        _intents.Setup(r => r.GetRevisionAsync(tenantId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = "int-1",
                Revision = 1,
                CanonicalPayload = """{"customer_id":"cus_manual"}""",
                PayloadHash = "h",
            });

        await CreateHandler().Handle(
            new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked,
                providerRef, "rpl_mat", "ops", "rc", "operator"),
            CancellationToken.None);

        _bookingRepo.Verify(b => b.MaterializeBookedAsync(
            It.Is<Booking>(x =>
                x.TenantId == tenantId &&
                x.BookingId == providerRef &&
                x.ProviderReference == providerRef &&
                // The identity a verification grant is later matched against — it must come from
                // the intent revision, not from the provider-reference fallback.
                x.CustomerId == "cus_manual" &&
                x.LifecycleStatus == BookingLifecycleStatus.Confirmed),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
