using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for consumer inbox deduplication rows.</summary>
public interface IInboxRepository
{
    Task AddAsync(ConsumerInbox inbox, CancellationToken ct = default);

    /// <summary>Returns the inbox row for the authoritative key
    /// <paramref name="tenantId"/> + <paramref name="consumerName"/> + <paramref name="messageId"/>
    /// (§4.3, §22.1), or null if absent. Tenant is scoped EXPLICITLY (not via the ambient query
    /// filter) so a worker/command not bound to this tenant resolves the correct row. Uses AsNoTracking.</summary>
    Task<ConsumerInbox?> GetAsync(
        string tenantId,
        string consumerName,
        string messageId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts the inbox row with <c>outcome = admitted</c> via
    /// <c>INSERT … ON CONFLICT DO NOTHING</c>.
    ///
    /// Returns <see langword="true"/> if THIS call admitted the message (won the insert),
    /// <see langword="false"/> if the row already existed (duplicate delivery → caller acks and
    /// drops).
    ///
    /// <para>MUST run inside the caller's ambient transaction so the admit and the consumer's
    /// durable state transition commit together (Inv-5, §5.1).  The caller
    /// (<c>InboxDeduplicationBehavior</c> in Cordia.Application) owns that transaction.</para>
    /// </summary>
    Task<bool> TryAdmitAsync(ConsumerInbox inbox, CancellationToken ct = default);
}
