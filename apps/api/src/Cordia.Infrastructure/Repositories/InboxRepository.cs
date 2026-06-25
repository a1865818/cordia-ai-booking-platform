using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IInboxRepository"/>.</summary>
public class InboxRepository(ApplicationDbContext db) : IInboxRepository
{
    public async Task AddAsync(ConsumerInbox inbox, CancellationToken ct = default)
    {
        db.ConsumerInbox.Add(inbox);
        await db.SaveChangesAsync(ct);
    }

    public Task<ConsumerInbox?> GetAsync(
        string consumerName,
        string messageId,
        CancellationToken ct = default)
        => db.ConsumerInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConsumerName == consumerName && e.MessageId == messageId, ct);
}
