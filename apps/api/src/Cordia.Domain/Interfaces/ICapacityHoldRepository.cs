using Cordia.Domain.Entities;

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
    /// Returns the latest capacity hold (by CreatedAt DESC, then HoldId DESC) for the given
    /// (tenantId, operationId, workflowStep) triple regardless of status, or null if none exists.
    /// Uses AsNoTracking with IgnoreQueryFilters.
    /// <para>
    /// "Latest" is deterministic: it reflects the current capacity state even when a hold was
    /// expired and a new one was created (re-reserve-after-expiry). Used by the finalize path
    /// so <see cref="ICapacityReservation.FinalizeAgainstOperationAsync"/> can identify the
    /// authoritative hold without the caller specifying a hold ID.
    /// </para>
    /// </summary>
    Task<CapacityHold?> GetLatestByOperationAndStepAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant query (uses IgnoreQueryFilters) that returns up to <paramref name="limit"/>
    /// active holds whose <c>expires_at</c> is at or before <paramref name="now"/>, ordered by
    /// <c>expires_at</c> ascending. Used by the hold-expiry sweeper (spec 07 §8).
    /// </summary>
    Task<IReadOnlyList<CapacityHold>> ListExpiredActiveAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
}
