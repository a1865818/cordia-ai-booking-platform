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
}
