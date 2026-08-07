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

    /// <summary>
    /// Takes the row lock on one hold WITHOUT changing it (<c>SELECT … FOR UPDATE</c>), so a
    /// transaction can acquire its capacity locks in a canonical order.
    ///
    /// <para><b>Why a lock with no write.</b> Every capacity path locks <c>capacity_holds</c> before
    /// <c>capacity_slots</c> — except a same-slot amendment, where
    /// <c>FinalizeAgainstOperationAsync</c> takes the slot first and only then does
    /// <c>AbsorbSameSlotAsync</c> reach for the PREVIOUS owner's hold. A concurrent cancel of the
    /// same booking takes those two rows in the opposite order (hold, then slot), so the two
    /// transactions can hold one row each and wait on the other: Postgres detects the cycle and
    /// aborts one side with <c>40P01</c>. No data is corrupted — the loser rolls back and
    /// redelivery re-runs it — but the work is thrown away for no reason.</para>
    ///
    /// <para>The inversion cannot be repaired inside <c>AbsorbSameSlotAsync</c>: by the time it
    /// runs, the slot is already locked by the finalize step of the same transaction. The previous
    /// owner's hold therefore has to be locked BEFORE the capacity phase touches the slot, and it
    /// must be locked without being modified — the <c>HoldNoLongerValid</c> branch that follows
    /// deliberately leaves the old seat alone, so releasing it early would strip a guest of the
    /// only capacity they hold.</para>
    ///
    /// <para>Returns silently when the hold does not exist: a missing row is not an error here, and
    /// there is nothing to order against.</para>
    /// </summary>
    Task LockAsync(string tenantId, string holdId, CancellationToken ct = default);
}
