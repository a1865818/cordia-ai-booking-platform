using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for idempotency fence records (§13.3, Inv-3).</summary>
public interface IIdempotencyRepository
{
    Task AddAsync(IdempotencyRecord record, CancellationToken ct = default);

    Task<IdempotencyRecord?> GetByKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Atomic creator-election primitive (§13.4): attempts to INSERT the record using
    /// <c>INSERT … ON CONFLICT (tenant_id, idempotency_key) DO NOTHING</c>.
    /// Returns <c>true</c> when this caller wins (1 row inserted), <c>false</c> when a
    /// concurrent writer already holds the key (0 rows inserted). Unlike a raw unique
    /// violation, ON CONFLICT DO NOTHING does NOT abort the surrounding transaction, so
    /// the caller can continue or roll back cleanly.
    /// Must be called inside an active <see cref="IUnitOfWork"/> transaction.
    /// </summary>
    Task<bool> ElectCreatorAsync(IdempotencyRecord record, CancellationToken ct = default);

    /// <summary>
    /// Releases a key whose logical operation no longer exists, but ONLY while the record still
    /// points at <paramref name="expectedOperationId"/>. Returns rows-affected (1 = voided,
    /// 0 = someone else already re-bound the key → the caller must re-read, not re-insert).
    ///
    /// <para>The conditional is the whole point. An unconditional delete would let a second
    /// caller, racing the first, remove the record the winner had just written for its FRESH
    /// operation — and then win its own election, producing exactly the duplicate the fence
    /// exists to prevent. Guarding on the operation id makes the loser's delete a no-op, so it
    /// falls through to the normal lost-election path (§13.4).</para>
    ///
    /// <para>This does not change spec 04's disposition for a LIVE operation: a same-key,
    /// same-hash replay of an operation whose product still exists is still answered with the
    /// existing operation. It only covers the case where that product was subsequently reversed,
    /// which makes the record a fence around nothing.</para>
    /// </summary>
    Task<int> TryVoidAsync(
        string tenantId,
        string idempotencyKey,
        string expectedOperationId,
        CancellationToken ct = default);
}
