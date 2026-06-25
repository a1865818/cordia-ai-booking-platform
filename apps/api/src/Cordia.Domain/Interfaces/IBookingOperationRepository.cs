using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for booking operations; version-token transitions use the §22.1 helper.</summary>
public interface IBookingOperationRepository
{
    Task AddAsync(BookingOperation operation, CancellationToken ct = default);

    Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE: transitions status from <paramref name="expectedVersion"/>
    /// to <paramref name="next"/>. Returns rows-affected (1 = won race, 0 = lost race → reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus next,
        CancellationToken ct = default);
}
