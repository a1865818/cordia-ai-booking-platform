using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IOutboxRepository"/>.</summary>
public class OutboxRepository(ApplicationDbContext db) : IOutboxRepository
{
    public async Task AddAsync(OutboxMessage message, CancellationToken ct = default)
    {
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimUnpublishedAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default)
    {
        // Background-worker claim: authoritative on the tenantId argument. The raw SQL's
        // `tenant_id = {0}` is the LOAD-BEARING tenant scope (it bounds the FOR UPDATE SKIP
        // LOCKED scan/lock to this tenant — never lock every tenant's rows). IgnoreQueryFilters
        // drops the ambient context filter, which EF would otherwise compose as an OUTER
        // `WHERE tenant_id = <context>`: for a worker not scoped to this tenant (or unset) that
        // outer filter would silently eliminate the just-claimed rows. Parameters are bound.
        var results = await db.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages WHERE tenant_id = {0} AND published_at IS NULL ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED",
                tenantId,
                limit)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        return results;
    }
}
