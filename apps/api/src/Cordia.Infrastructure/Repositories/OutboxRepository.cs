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
        // The `tenant_id = {0}` predicate is LOAD-BEARING and must stay inside this raw SQL.
        // EF composes the global tenant filter as an OUTER query over this statement, so the
        // inner FOR UPDATE SKIP LOCKED scans/locks rows BEFORE the outer filter applies. Without
        // the inner predicate, the claim would lock every tenant's unpublished rows (tenant
        // isolation + lock-amplification bug). Parameters are bound (no SQL injection).
        var results = await db.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages WHERE tenant_id = {0} AND published_at IS NULL ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED",
                tenantId,
                limit)
            .ToListAsync(ct);

        return results;
    }
}
