using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for authoritative, tenant-scoped bookings.</summary>
public interface IBookingRepository
{
    Task AddAsync(Booking booking, CancellationToken ct = default);

    Task MaterializeBookedAsync(Booking booking, CancellationToken ct = default);

    Task<Booking?> GetByIdAsync(string tenantId, string bookingId, CancellationToken ct = default);

    Task<Booking?> GetByReferenceAsync(string tenantId, string bookingReference, CancellationToken ct = default);

    Task<IReadOnlyList<Booking>> ListByCustomerAsync(
        string tenantId,
        string customerId,
        int limit = 20,
        CancellationToken ct = default);

    Task<long?> GetCurrentVersionAsync(string tenantId, string bookingId, CancellationToken ct = default);

    Task<bool> HasExpectedVersionAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>
    /// RELEASES the booking's lifecycle: moves it to <paramref name="next"/> and clears
    /// <see cref="Booking.PendingOperationId"/>, guarded by <paramref name="expectedVersion"/>.
    ///
    /// <para>Clearing the fence is unconditional because every transition through this method is a
    /// release — a pending state being settled, reverted, or overtaken by a cancellation. Use
    /// <see cref="TryParkPendingAsync"/> to CLAIM a pending state; parking through here would leave
    /// the row saying a change is outstanding with nobody named as its owner, which is precisely the
    /// state the fence exists to prevent.</para>
    ///
    /// <para>This method does NOT check who parked the state, and that is deliberate: it carries the
    /// SETTLEMENTS, where the outcome is authoritative regardless of ownership. A cancellation
    /// reaching <see cref="BookingLifecycleStatus.Cancelled"/> must overtake a modification's
    /// pending state — the booking is gone, and refusing on the grounds that somebody else parked it
    /// would strand a cancelled booking at ModificationPending. Use
    /// <see cref="TryReleasePendingAsync"/> for the other case: a RETRACTION back to
    /// <see cref="BookingLifecycleStatus.Confirmed"/>, where the whole claim being made is "nothing
    /// happened, so put it back as it was" — which is only this operation's to say if it is the one
    /// that parked it.</para>
    /// </summary>
    /// <returns>Rows affected: 1 = won, 0 = lost the version race (reload and re-evaluate).</returns>
    Task<int> TryTransitionLifecycleAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        BookingLifecycleStatus next,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// RELEASES a pending lifecycle only if <paramref name="releasingOperationId"/> still owns it:
    /// moves the booking to <paramref name="next"/> and clears
    /// <see cref="Booking.PendingOperationId"/>, guarded by <paramref name="expectedVersion"/>,
    /// <paramref name="expectedStatus"/> AND the current owner — all in one statement.
    ///
    /// <para>The ownership test belongs in the UPDATE, not in the caller. Reading the booking,
    /// deciding in memory that this operation is the parker, and then issuing a version-guarded
    /// write leaves a window between the two: the guard proves only that nobody bumped the version,
    /// so the decision rests on the assumption that every writer of
    /// <c>pending_operation_id</c> routes through a version-token UPDATE. The
    /// moment one does not — a backfill, a repair script, a tracked-entity save — the check is
    /// reading a snapshot while the write lands on a row that has since changed hands, and this
    /// operation retracts a pending state another one is actively relying on. Evaluating the owner
    /// in the same statement that performs the release closes the window by construction: there is
    /// no interval for the owner to change in.</para>
    ///
    /// <para>A NULL owner still releases, deliberately. Rows parked before
    /// <see cref="Booking.PendingOperationId"/> existed name nobody, and refusing them would strand
    /// the booking at a pending status permanently (Inv-7/Inv-8) — the worse failure, and the one
    /// that never self-corrects. The race being closed here is transient and only reachable under
    /// genuine multi-actor concurrency, so the fail-open is bounded to rows that predate the column.
    /// Everything parked since carries its parker and is fully fenced.</para>
    /// </summary>
    /// <returns>
    /// Rows affected: 1 = won, 0 = lost — the version moved, the booking left
    /// <paramref name="expectedStatus"/>, or another operation now owns the pending state. The
    /// caller cannot distinguish the three, and must not treat 0 as "released".
    /// </returns>
    Task<int> TryReleasePendingAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        BookingLifecycleStatus expectedStatus,
        string releasingOperationId,
        BookingLifecycleStatus next,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// CLAIMS a pending lifecycle for <paramref name="pendingOperationId"/>: moves the booking to
    /// <paramref name="next"/> and stamps that operation as the parker, guarded by
    /// <paramref name="expectedVersion"/>.
    ///
    /// <para>The version guard alone does not serialize two mutations — a second one presenting the
    /// already-pending version parks the booking again quite legitimately. Recording WHO parked it
    /// is what lets the loser of that race recognise that the pending state it is looking at is not
    /// its own.</para>
    /// </summary>
    /// <returns>Rows affected: 1 = won, 0 = lost the version race.</returns>
    Task<int> TryParkPendingAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        BookingLifecycleStatus next,
        string pendingOperationId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records guest attendance. Returns rows affected: 1 = updated, 0 = no such booking.
    /// Does not bump the lifecycle version — attendance is orthogonal to modify/cancel fencing.
    /// </summary>
    Task<int> TrySetAttendanceAsync(
        string tenantId,
        string bookingId,
        AttendanceStatus attendance,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
}
