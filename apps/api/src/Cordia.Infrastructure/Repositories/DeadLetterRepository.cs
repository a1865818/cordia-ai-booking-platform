using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IDeadLetterRepository"/> (spec 05 §5.3, T7).</summary>
public class DeadLetterRepository(ApplicationDbContext db) : IDeadLetterRepository
{
    public async Task AddAsync(DeadLetterMessage message, CancellationToken ct = default)
    {
        db.DeadLetterMessages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    public Task<DeadLetterMessage?> GetAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default)
        => db.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId
                  && e.MessageId == messageId
                  && e.ConsumerName == consumerName,
                ct);

    public Task<int> SetReplayStateAsync(
        string tenantId,
        string messageId,
        string consumerName,
        DlqReplayState state,
        CancellationToken ct = default)
        => db.DeadLetterMessages
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && e.MessageId == messageId
                     && e.ConsumerName == consumerName)
            .ExecuteUpdateAsync(
                b => b.SetProperty(e => e.ReplayState, state),
                ct);

    public Task<int> TryMarkReplayedAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default)
        // Conditional on replay_state = Quarantined: the WHERE clause is the atomic claim, so only
        // one of N concurrent replays affects a row (the rest get 0 rows → suppress duplicate effects).
        => db.DeadLetterMessages
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && e.MessageId == messageId
                     && e.ConsumerName == consumerName
                     && e.ReplayState == DlqReplayState.Quarantined)
            .ExecuteUpdateAsync(
                b => b.SetProperty(e => e.ReplayState, DlqReplayState.Replayed),
                ct);
}
