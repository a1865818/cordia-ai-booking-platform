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
}
