using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for booking operations; version-token transitions use the §22.1 helper.</summary>
public interface IBookingOperationRepository
{
    Task AddAsync(BookingOperation operation, CancellationToken ct = default);

    Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE (§6.2): transitions to <paramref name="next"/> only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Guarding both the expected status and version makes an illegal state-graph transition
    /// return 0 even if versions coincide. Returns rows-affected (1 = won race, 0 = lost race → reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        OperationStatus next,
        CancellationToken ct = default);
}
