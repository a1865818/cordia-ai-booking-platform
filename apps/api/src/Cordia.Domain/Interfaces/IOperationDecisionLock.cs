namespace Cordia.Domain.Interfaces;

/// <summary>
/// A per-operation lock held for the rest of the current database transaction (#108).
///
/// <para>Serialises the two decisions that must not interleave: the dispatch fence
/// (<c>Planned → Dispatching</c>, the last write before an irreversible provider call) and the
/// cancel-before-create CAS (<c>Finalizing → CompensationRequired</c>). Each re-reads state and then
/// writes; without a shared lock both could read the other's "before" and both proceed.</para>
/// </summary>
public interface IOperationDecisionLock
{
    /// <summary>
    /// Blocks until this transaction holds the lock for (<paramref name="tenantId"/>,
    /// <paramref name="operationId"/>); released at commit or rollback. Throws when called outside a
    /// transaction, where the lock would be released the moment it was taken.
    /// </summary>
    Task AcquireAsync(string tenantId, string operationId, CancellationToken ct = default);
}
