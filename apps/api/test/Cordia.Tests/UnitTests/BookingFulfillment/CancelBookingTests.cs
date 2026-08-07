using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.CancelBooking;
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
/// Unit tests for <see cref="CancelBookingCommandHandler"/> (spec 08 §5.3, spec 06 §11).
///
/// The defect these guard against: before this handler existed, a cancel operation was driven
/// through the create pipeline and called <c>CreateBookingAsync</c>, producing a second booking
/// and leaving the original stuck in <c>cancellation_pending</c>.
/// </summary>
[Trait("Category", "Unit")]
public class CancelBookingTests
{
    private const string TenantId = "tenant-a";
    private const string OperationId = "op_cancel_1";
    private const string BookingId = "bk_1";
    private const string OriginOperationId = "op_create_1";
    private const string ProviderRef = "prov_ref_1";

    private readonly Mock<IBookingOperationRepository> _ops = new();
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IWorkflowStepRepository> _steps = new();
    private readonly Mock<IBookingProviderAdapter> _adapter = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<ICapacityReservation> _capacity = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public CancelBookingTests()
    {
        // Run the transaction bodies inline so the assertions see their effects.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((body, ct) => body(ct));

        _steps.Setup(s => s.GetByOperationAndTypeAsync(
                TenantId, OperationId, "cancel", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);

        _ops.Setup(o => o.GetBookedOperationForBookingAsync(
                TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OriginOperationId, OperationStatus.Booked, OperationTypeEnum.Create));
    }

    private CancelBookingCommandHandler CreateHandler() => new(
        _ops.Object, _bookings.Object, _steps.Object, _adapter.Object,
        _outbox.Object, _audit.Object, _capacity.Object, _uow.Object, new Metrics());

    private static BookingOperation Operation(
        string operationId,
        OperationStatus status,
        OperationTypeEnum type = OperationTypeEnum.Cancel,
        string? bookingId = BookingId) => new()
        {
            TenantId = TenantId,
            OperationId = operationId,
            IntentId = "int_1",
            IntentRevision = 1,
            OperationType = type,
            Status = status,
            BookingId = bookingId,
            Version = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static Booking BookingRow(
        BookingLifecycleStatus lifecycle = BookingLifecycleStatus.CancellationPending,
        string? providerRef = ProviderRef) => new()
        {
            TenantId = TenantId,
            BookingId = BookingId,
            BookingReference = "CORD-1",
            ProviderReference = providerRef,
            CustomerId = "cust_1",
            LifecycleStatus = lifecycle,
            Version = 5,
        };

    /// <summary>Wires the operation reads and a successful version-guarded transition.</summary>
    private void ArrangeHappyPath(
        BookingLifecycleStatus lifecycle = BookingLifecycleStatus.CancellationPending,
        string? providerRef = ProviderRef)
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OperationId, OperationStatus.Finalizing));
        _bookings.Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(lifecycle, providerRef));
        _ops.Setup(o => o.TryTransitionStatusAsync(
                TenantId, OperationId, It.IsAny<long>(),
                OperationStatus.Finalizing, OperationStatus.Booked,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        // Must be stubbed explicitly: an unstubbed Moq int returns 0, which is exactly the
        // "another writer moved the row" signal — every happy-path test would silently be
        // asserting against a booking that never transitioned.
        _bookings.Setup(b => b.TryTransitionLifecycleAsync(
                TenantId, BookingId, It.IsAny<long>(),
                BookingLifecycleStatus.Cancelled, It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    // ---------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CancelsWithTheProvider_ThenCancelsTheBooking_AndFreesCapacity()
    {
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created, ProviderRef));

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, outcome);

        // It must CANCEL, never CREATE — that was the whole defect.
        _adapter.Verify(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // The booking row is what carries the cancellation (MB-Inv-6 — a transition, not a delete).
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            TenantId, BookingId, It.IsAny<long>(),
            BookingLifecycleStatus.Cancelled, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);

        // The seat goes back — released against the ORIGINATING operation, which owns the hold.
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            TenantId, OriginOperationId, ReleaseReason.ProviderReleased, It.IsAny<CancellationToken>()), Times.Once);

        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == CancelBookingCommandHandler.CancelConfirmedEventType),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.cancelled"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ProviderResultKind.Created)]
    [InlineData(ProviderResultKind.AlreadyExists)]
    [InlineData(ProviderResultKind.NotFound)]
    public async Task TreatsEveryAcknowledgedProviderOutcomeAsCancelled(ProviderResultKind kind)
    {
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(kind));

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, outcome);
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            TenantId, BookingId, It.IsAny<long>(),
            BookingLifecycleStatus.Cancelled, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------------
    // Inv-7: ambiguity reconciles, never guesses
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(ProviderResultKind.Ambiguous)]
    [InlineData(ProviderResultKind.Transient)]
    [InlineData(ProviderResultKind.PermanentError)]
    [InlineData(ProviderResultKind.AuthError)]
    public async Task UncertainProviderOutcome_ReconcilesAndChangesNothingElse(ProviderResultKind kind)
    {
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(kind));
        _ops.Setup(o => o.TryTransitionStatusAsync(
                TenantId, OperationId, It.IsAny<long>(),
                OperationStatus.Finalizing, OperationStatus.Reconciling,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, outcome);

        // We do not know whether the provider cancelled: the booking must NOT be marked
        // cancelled, the seat must NOT be given back, and the customer must NOT be told.
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // Spec 08 §9.4.4 — already cancelled settles without re-invoking the provider
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AlreadyCancelledBooking_SettlesWithoutCallingTheProvider()
    {
        ArrangeHappyPath(lifecycle: BookingLifecycleStatus.Cancelled);

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, outcome);
        _adapter.Verify(a => a.CancelBookingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Already in the target state — no redundant lifecycle write.
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        // And above all: only ONE cancellation of a booking is news. Notifying again would send
        // the guest a second "your cancellation is confirmed" for something already told.
        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        // The audit event is still written — internal record, carries reason=already_cancelled.
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoProviderReference_SettlesWithoutCallingTheProvider()
    {
        ArrangeHappyPath(providerRef: null);

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, outcome);
        _adapter.Verify(a => a.CancelBookingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            TenantId, BookingId, It.IsAny<long>(),
            BookingLifecycleStatus.Cancelled, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------------
    // Inv-5: duplicate delivery
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LostTransitionRace_WritesNothingDependent()
    {
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));
        // A concurrent delivery already settled the operation.
        _ops.Setup(o => o.TryTransitionStatusAsync(
                TenantId, OperationId, It.IsAny<long>(),
                OperationStatus.Finalizing, OperationStatus.Booked,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, outcome);
        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BookingThatWillNotTransition_RollsBackInsteadOfAnnouncingACancellation()
    {
        // The booking row is what carries the cancellation. If a concurrent writer keeps moving
        // it, releasing the seat and emitting `cancel_confirmed` would free capacity for a
        // booking that is still live — and the operation would be terminal, so nothing would
        // ever re-drive it. The whole transaction must fail instead.
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));
        _bookings.Setup(b => b.TryTransitionLifecycleAsync(
                TenantId, BookingId, It.IsAny<long>(),
                BookingLifecycleStatus.Cancelled, It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await Assert.ThrowsAsync<StaleBookingVersionException>(() => CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None));

        _capacity.Verify(c => c.ReleaseFinalizedByOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Losing the race to a writer that reached the SAME end state is not a failure: the booking is
    /// cancelled, so this delivery finishes the operation normally — and says NOTHING to the guest.
    ///
    /// <para>The notification is the point. This is the concurrent-cancel shape exactly: the
    /// booking read as CancellationPending before the provider call, so the handler's
    /// `alreadyCancelled` flag was false, and it stayed false while the transaction found the row
    /// already Cancelled. Emitting on that stale flag sent a SECOND "your cancellation is
    /// confirmed" for a cancellation the winning transaction had already announced. Eligibility now
    /// comes from whether THIS transaction performed the transition, which is the only fact that
    /// distinguishes the two callers.</para>
    /// </summary>
    [Fact]
    public async Task BookingCancelledByAConcurrentWriter_SettlesWithoutRetryingOrNotifyingTwice()
    {
        ArrangeHappyPath(lifecycle: BookingLifecycleStatus.Cancelled);
        _bookings.SetupSequence(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(BookingLifecycleStatus.CancellationPending, ProviderRef))
            .ReturnsAsync(BookingRow(BookingLifecycleStatus.Cancelled, ProviderRef));
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, outcome);
        _bookings.Verify(b => b.TryTransitionLifecycleAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        // The internal record is still written — that is the operation's own trail, not news.
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReusesAnExistingCancelStep_RatherThanCreatingASecond()
    {
        ArrangeHappyPath();
        _adapter.Setup(a => a.CancelBookingAsync("default", ProviderRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));
        _steps.Setup(s => s.GetByOperationAndTypeAsync(
                TenantId, OperationId, "cancel", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStep
            {
                TenantId = TenantId,
                WorkflowStepId = "ws_existing",
                OperationId = OperationId,
                StepType = "cancel",
                Status = WorkflowStepStatus.InProgress,
                ProviderIdempotencyKey = string.Empty,
                AttemptCount = 1,
                Version = 0,
            });

        await CreateHandler().Handle(new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        _steps.Verify(s => s.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // Guards
    // ---------------------------------------------------------------------

    [Fact]
    public async Task IgnoresAnOperationFromAnotherTenant()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = "tenant-b",
                OperationId = OperationId,
                OperationType = OperationTypeEnum.Cancel,
                Status = OperationStatus.Finalizing,
                BookingId = BookingId,
            });

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, outcome);
        _adapter.Verify(a => a.CancelBookingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IgnoresANonCancelOperation()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OperationId, OperationStatus.Finalizing, OperationTypeEnum.Create));

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, outcome);
        _adapter.Verify(a => a.CancelBookingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IgnoresAnOperationThatHasAlreadyLeftFinalizing()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OperationId, OperationStatus.Booked));

        var outcome = await CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, outcome);
    }

    [Fact]
    public async Task PoisonsACancelWithNoTargetBooking()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OperationId, OperationStatus.Finalizing, bookingId: null));

        // Retrying cannot conjure a target — this is a defect, so it must quarantine (Inv-14)
        // rather than loop forever.
        await Assert.ThrowsAsync<PoisonMessageException>(() => CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None));
    }

    [Fact]
    public async Task PoisonsACancelWhoseTargetBookingDoesNotExist()
    {
        _ops.Setup(o => o.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operation(OperationId, OperationStatus.Finalizing));
        _bookings.Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Booking?)null);

        await Assert.ThrowsAsync<PoisonMessageException>(() => CreateHandler().Handle(
            new CancelBookingCommand(TenantId, OperationId), CancellationToken.None));
    }
}
