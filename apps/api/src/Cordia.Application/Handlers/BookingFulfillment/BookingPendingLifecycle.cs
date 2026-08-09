using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.BookingFulfillment;

/// <summary>
/// Releases the booking-row lifecycle a MUTATION parked while it was in flight, when that mutation
/// ends without having happened.
///
/// <para><c>POST /api/Bookings/{id}/modifications</c> parks the booking at
/// <see cref="BookingLifecycleStatus.ModificationPending"/> and <c>.../cancellations</c> at
/// <see cref="BookingLifecycleStatus.CancellationPending"/>, so concurrent writers can see a change
/// in flight. Those two front doors are the ONLY writers of those statuses, and only the success
/// paths ever cleared them — every terminal FAILURE route left the operation reading
/// <c>rejected</c>/<c>failed_final</c> while the booking row went on telling every reader a change
/// was still outstanding. Nothing downstream of a terminal operation ever looks at the booking
/// again, so it was permanent: exactly the spec-11 defect family where an operation and the row it
/// acted on disagree about the same event (Inv-7/Inv-8).</para>
///
/// <para>Failed means NOTHING HAPPENED, so both revert to <see cref="BookingLifecycleStatus.Confirmed"/>:
/// the amendment was not applied and the cancellation did not land, leaving the original booking as
/// it was. Guarded on the pending statuses, so a booking a better-informed writer moved to
/// <c>Cancelled</c> keeps that outcome; version-guarded, so losing a race is a benign no-op rather
/// than an overwrite.</para>
///
/// <para>Call INSIDE the transition's own transaction, after the terminal transition won its rows —
/// the operation's outcome and the booking's lifecycle must commit together or not at all.</para>
/// </summary>
public static class BookingPendingLifecycle
{
    /// <summary>
    /// The operation types that park a pending lifecycle. A create has no existing booking to park.
    /// </summary>
    public static bool IsMutation(BookingOperation op)
        => op.OperationType is Domain.Generated.OperationTypeEnum.Cancel
            or Domain.Generated.OperationTypeEnum.Modify
            or Domain.Generated.OperationTypeEnum.Reschedule;

    /// <summary>
    /// Reverts <paramref name="op"/>'s booking to Confirmed if this operation is a mutation and the
    /// booking is still parked pending. No-op for a create, for an operation with no booking, and
    /// for a booking that is not pending.
    /// </summary>
    public static async Task RevertOnFailureAsync(
        IBookingRepository bookingRepo,
        string tenantId,
        BookingOperation op,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!IsMutation(op) || string.IsNullOrWhiteSpace(op.BookingId))
            return;

        var target = await bookingRepo.GetByIdAsync(tenantId, op.BookingId, ct);

        if (target is not
            {
                LifecycleStatus: BookingLifecycleStatus.ModificationPending
                        or BookingLifecycleStatus.CancellationPending
            })
        {
            return;
        }

        await bookingRepo.TryTransitionLifecycleAsync(
            tenantId, target.BookingId, target.Version,
            BookingLifecycleStatus.Confirmed, now, ct);
    }
}
