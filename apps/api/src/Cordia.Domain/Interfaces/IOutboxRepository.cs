using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for transactional outbox messages.</summary>
public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// Selects up to <paramref name="limit"/> unpublished messages for <paramref name="tenantId"/>
    /// using FOR UPDATE SKIP LOCKED, so concurrent publishers do not double-claim.
    /// Must run inside the caller's ambient transaction.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimUnpublishedAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default);
}
