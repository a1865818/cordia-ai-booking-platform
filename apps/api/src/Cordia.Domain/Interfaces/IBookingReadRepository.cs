using Cordia.Domain.Bookings;

namespace Cordia.Domain.Interfaces;

/// <summary>Tenant-scoped read model repository for Manage Booking discovery and details.</summary>
public interface IBookingReadRepository
{
    Task<IReadOnlyList<ManageBookingCandidate>> FindManageBookingCandidatesAsync(
        string tenantId,
        ManageBookingDiscoveryHints hints,
        int limit = 10,
        CancellationToken ct = default);

    Task<BookingProjection?> GetBookingProjectionByBookingIdAsync(
        string tenantId,
        string bookingId,
        CancellationToken ct = default);

    Task<BookingProjection?> GetBookingProjectionByOperationIdAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
