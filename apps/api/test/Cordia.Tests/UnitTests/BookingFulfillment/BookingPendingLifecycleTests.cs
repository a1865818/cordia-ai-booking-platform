using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="BookingPendingLifecycle"/>'s ownership fence.
///
/// <para>c7f08eb closed the CROSS-type clobber — a failed Modify no longer retracts a
/// <c>CancellationPending</c> a newer cancel parked. The SAME-type case stayed open, and these pin
/// it: two modifications (or two cancellations) can both be live against one booking, because
/// <c>POST /api/Bookings/{id}/modifications</c> parks under a version-only guard with no "booking
/// must be Confirmed" precondition. Whichever failed first found its own type's pending state,
/// matched on status and version, and reverted the booking to Confirmed while the other was
/// genuinely in flight.</para>
/// </summary>
[Trait("Category", "Unit")]
public class BookingPendingLifecycleTests
{
    private const string TenantId = "t1";
    private const string BookingId = "bk-1";

    private readonly Mock<IBookingRepository> _bookings = new();

    private static BookingOperation MakeOp(string operationId, OperationTypeEnum type) =>
        new()
        {
            TenantId = TenantId,
            OperationId = operationId,
            BookingId = BookingId,
            OperationType = type,
        };

    private void GivenBooking(BookingLifecycleStatus status, string? pendingOperationId, long version = 9) =>
        _bookings.Setup(r => r.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                LifecycleStatus = status,
                PendingOperationId = pendingOperationId,
                Version = version,
            });

    private Task WhenFailed(BookingOperation op) =>
        BookingPendingLifecycle.RevertOnFailureAsync(
            _bookings.Object, TenantId, op, DateTimeOffset.UnixEpoch, CancellationToken.None);

    private void VerifyReverted(Times times) =>
        _bookings.Verify(r => r.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                BookingLifecycleStatus.Confirmed, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            times);

    /// <summary>
    /// The release must never go out through the unconditional transition — that one clears the
    /// pending state on a version match alone, which is the write this fence exists to prevent.
    /// </summary>
    private void VerifyNeverReleasedUnconditionally() =>
        _bookings.Verify(r => r.TryTransitionLifecycleAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);

    /// <summary>
    /// The defect: Modify A parks (v→V+1), Modify B parks again on V+1 (v→V+2) and runs, A then
    /// fails and finds ModificationPending with a matching version — B's parking, not its own.
    /// Reverting there tells every reader no change is outstanding while B is still running, and
    /// lets a third writer in on the Confirmed version.
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_LeavesAModificationAnotherOperationParked()
    {
        GivenBooking(BookingLifecycleStatus.ModificationPending, pendingOperationId: "op-modify-B");

        await WhenFailed(MakeOp("op-modify-A", OperationTypeEnum.Modify));

        VerifyReverted(Times.Never());
    }

    /// <summary>The same race between two cancellations, which park the other pending state.</summary>
    [Fact]
    public async Task RevertOnFailure_LeavesACancellationAnotherOperationParked()
    {
        GivenBooking(BookingLifecycleStatus.CancellationPending, pendingOperationId: "op-cancel-B");

        await WhenFailed(MakeOp("op-cancel-A", OperationTypeEnum.Cancel));

        VerifyReverted(Times.Never());
    }

    /// <summary>
    /// The fence must not cost the release its actual job: an operation that parked the state still
    /// retracts it, version-guarded, or the booking stays "modification pending" forever
    /// (Inv-7/Inv-8).
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_RetractsThePendingStateItParkedItself()
    {
        GivenBooking(BookingLifecycleStatus.ModificationPending, pendingOperationId: "op-modify-A", version: 9);

        await WhenFailed(MakeOp("op-modify-A", OperationTypeEnum.Modify));

        _bookings.Verify(r => r.TryReleasePendingAsync(
                TenantId, BookingId, 9,
                BookingLifecycleStatus.ModificationPending, "op-modify-A",
                BookingLifecycleStatus.Confirmed, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyNeverReleasedUnconditionally();
    }

    /// <summary>
    /// The fence has to travel WITH the write, not just precede it.
    ///
    /// <para>The in-memory check reads a snapshot; between it and the UPDATE another operation can
    /// park the booking. A version guard only closes that window for writers that bump the version,
    /// so the release must carry its own <c>pending_operation_id</c> predicate and let the database
    /// decide. Pinning the exact arguments is the only way to assert that from a unit test: the
    /// statement must name the state it expects to find and the operation it claims to be.</para>
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_ReleasesConditionallyOnTheOwnerAndTheParkedState()
    {
        GivenBooking(BookingLifecycleStatus.CancellationPending, pendingOperationId: "op-cancel-A", version: 3);

        await WhenFailed(MakeOp("op-cancel-A", OperationTypeEnum.Cancel));

        _bookings.Verify(r => r.TryReleasePendingAsync(
                TenantId, BookingId, 3,
                // The state this operation parked, and the operation itself — both become WHERE
                // clauses, so a row that has since moved or changed hands matches nothing.
                BookingLifecycleStatus.CancellationPending, "op-cancel-A",
                BookingLifecycleStatus.Confirmed, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyNeverReleasedUnconditionally();
    }

    /// <summary>
    /// A lost CAS is a benign no-op, not an escalation. Zero rows means the booking moved, was
    /// settled, or changed hands — in every case the pending state stopped being this operation's
    /// to retract, and the terminal transition that called us already carried the outcome.
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_SwallowsALostRelease()
    {
        GivenBooking(BookingLifecycleStatus.ModificationPending, pendingOperationId: "op-modify-A");
        _bookings.Setup(r => r.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await WhenFailed(MakeOp("op-modify-A", OperationTypeEnum.Modify));

        VerifyReverted(Times.Once());
    }

    /// <summary>
    /// An UNOWNED pending state still releases — deliberately fail-open.
    ///
    /// <para>Rows parked before <c>pending_operation_id</c> existed name nobody. Refusing them would
    /// strand a booking at ModificationPending permanently, which is the worse failure and the one
    /// that never self-corrects; the race this fence closes is transient and only reachable under
    /// genuine multi-actor concurrency. Everything parked since the column landed carries its
    /// parker.</para>
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_RetractsAnUnownedPendingStateFromBeforeTheFenceExisted()
    {
        GivenBooking(BookingLifecycleStatus.ModificationPending, pendingOperationId: null);

        await WhenFailed(MakeOp("op-modify-A", OperationTypeEnum.Modify));

        VerifyReverted(Times.Once());
    }

    /// <summary>
    /// The cross-type guard c7f08eb added still holds — the ownership fence sits alongside it, not
    /// instead of it. A failed Modify against a booking a cancel parked is refused on TYPE before
    /// the owner is ever consulted.
    /// </summary>
    [Fact]
    public async Task RevertOnFailure_StillLeavesTheOtherPendingStateAlone()
    {
        GivenBooking(BookingLifecycleStatus.CancellationPending, pendingOperationId: "op-modify-A");

        await WhenFailed(MakeOp("op-modify-A", OperationTypeEnum.Modify));

        VerifyReverted(Times.Never());
    }

    /// <summary>A reschedule parks nothing, so it retracts nothing — whoever owns the row.</summary>
    [Fact]
    public async Task RevertOnFailure_RetractsNothingForAReschedule()
    {
        GivenBooking(BookingLifecycleStatus.ModificationPending, pendingOperationId: null);

        await WhenFailed(MakeOp("op-reschedule", OperationTypeEnum.Reschedule));

        VerifyReverted(Times.Never());
    }
}
