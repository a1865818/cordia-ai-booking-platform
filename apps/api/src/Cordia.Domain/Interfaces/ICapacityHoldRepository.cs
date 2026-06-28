using Cordia.Domain.Capacity;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for capacity hold entities.</summary>
public interface ICapacityHoldRepository
{
    /// <summary>Returns the hold for the given (tenantId, holdId) pair, or null if not found. Uses AsNoTracking.</summary>
    Task<CapacityHold?> GetAsync(string tenantId, string holdId, CancellationToken ct = default);

    /// <summary>Inserts a new capacity hold and flushes within the ambient transaction.</summary>
    Task AddAsync(CapacityHold hold, CancellationToken ct = default);

    /// <summary>
    /// Returns all active holds for the given (tenantId, operationId) pair. Uses AsNoTracking.
    /// Used by release-by-operation and finalize-against-hold lookup paths.
    /// </summary>
    Task<IReadOnlyList<CapacityHold>> GetActiveByOperationAsync(string tenantId, string operationId, CancellationToken ct = default);

    /// <summary>
    /// Returns any capacity hold (regardless of status) for the given (tenantId, operationId,
    /// workflowStep) triple, or null if none exists. Uses AsNoTracking.
    /// Used by the finalize path to detect whether an op is first-party-tracked even after the
    /// hold has been released or expired (§6.2 expiry-races-finalize scenario).
    /// </summary>
    Task<CapacityHold?> GetByOperationAndStepAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant query (uses IgnoreQueryFilters) that returns up to <paramref name="limit"/>
    /// active holds whose <c>expires_at</c> is at or before <paramref name="now"/>, ordered by
    /// <c>expires_at</c> ascending. Used by the hold-expiry sweeper (spec 07 §8).
    /// </summary>
    Task<IReadOnlyList<CapacityHold>> ListExpiredActiveAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
}
