namespace Cordia.Domain.Interfaces;

/// <summary>
/// The single implementation of the <b>Cordia Capacity Lock Order</b> (CLO) — the rule that makes a
/// capacity deadlock unreachable rather than merely unlikely (spec 07 §14).
///
/// <para><b>The rule.</b> Within one transaction:</para>
/// <list type="number">
///   <item>acquire every <c>capacity_holds</c> row lock BEFORE any <c>capacity_slots</c> row lock;</item>
///   <item>acquire multiple <c>capacity_holds</c> rows in ascending <c>hold_id</c> order;</item>
///   <item>acquire multiple <c>capacity_slots</c> rows in ascending <c>slot_id</c> order;</item>
///   <item><b>carve-out:</b> INSERTing a NEW hold row acquires no pre-existing row lock and so cannot
///     join a wait cycle — <c>ReserveAsync</c>'s slot-update-then-hold-insert is compliant.</item>
/// </list>
///
/// <para><b>Why an interface and not a convention.</b> A rule that each call site re-derives is a
/// rule that each call site can get wrong, and it has been got wrong twice: once as a hold↔slot
/// inversion between modify and cancel, and once as a slot↔slot cycle between two reschedules
/// running in opposite directions. Callers of this type say WHICH rows they will touch; they never
/// say in what order. There is exactly one place left that can order them wrongly.</para>
///
/// <para><b>Acquire everything up front.</b> Pass every hold and every slot the transaction will
/// touch, before it writes any of them. Later writes then re-touch rows this transaction already
/// holds, so they acquire nothing new and cannot re-order anything. Acquiring lazily — as each step
/// discovers what it needs — is what produced both cycles.</para>
///
/// <para>Locks are released when the ambient transaction commits or rolls back. This type never
/// writes; taking a lock must not be confused with claiming or releasing a seat.</para>
/// </summary>
public interface ICapacityLockOrder
{
    /// <summary>
    /// Acquires row locks on the given holds and slots in canonical order. Null/empty ids are
    /// skipped and duplicates collapse, so callers can pass optional ids straight through without
    /// pre-filtering. Safe to call more than once in a transaction: a lock already held is a no-op.
    /// </summary>
    Task AcquireAsync(
        string tenantId,
        IEnumerable<string?> holdIds,
        IEnumerable<string?> slotIds,
        CancellationToken ct = default);
}
