using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for idempotency fence records (§13.3, Inv-3).</summary>
public interface IIdempotencyRepository
{
    Task AddAsync(IdempotencyRecord record, CancellationToken ct = default);

    Task<IdempotencyRecord?> GetByKeyAsync(string idempotencyKey, CancellationToken ct = default);
}
