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

    /// <summary>
    /// Cross-tenant worker claim (§5.1): atomically selects up to <paramref name="limit"/>
    /// unpublished rows (WHERE published_at IS NULL AND lease free/expired), sets lease fields,
    /// increments publish_attempts, and commits — all in its own short transaction.
    /// SKIP LOCKED ensures concurrent publishers do not double-claim the same row.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a message as published (§5.1 mark phase): sets published_at, clears the lease.
    /// Uses ExecuteUpdateAsync (no tracked entity needed) — runs in its own implicit transaction.
    /// </summary>
    Task MarkPublishedAsync(
        string tenantId,
        string messageId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if an outbox row with the given <paramref name="messageId"/> already exists
    /// for <paramref name="tenantId"/> (published or not). Used for deterministic deduplication.
    /// </summary>
    Task<bool> ExistsAsync(string tenantId, string messageId, CancellationToken ct = default);
}
