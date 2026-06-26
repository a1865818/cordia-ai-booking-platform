using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for consumer inbox deduplication rows.</summary>
public interface IInboxRepository
{
    Task AddAsync(ConsumerInbox inbox, CancellationToken ct = default);

    /// <summary>Returns the inbox row for <paramref name="consumerName"/> + <paramref name="messageId"/>,
    /// or null if absent. Uses AsNoTracking.</summary>
    Task<ConsumerInbox?> GetAsync(
        string consumerName,
        string messageId,
        CancellationToken ct = default);
}
