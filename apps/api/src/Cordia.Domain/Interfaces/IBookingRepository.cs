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

    Task<int> TryTransitionLifecycleAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        BookingLifecycleStatus next,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
}
