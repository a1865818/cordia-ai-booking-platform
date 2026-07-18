using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IOutboxRepository"/>.</summary>
public class OutboxRepository(ApplicationDbContext db) : IOutboxRepository
{
    public async Task AddAsync(OutboxMessage message, CancellationToken ct = default)
    {
        // Inv-12 choke point: no queue envelope may carry unmasked sensitive data. Control-plane
        // ids/hashes are exempt so idempotency joins and Inv-2 hash comparisons round-trip.
        message.Payload = SensitiveDataMask.SanitizeJson(message.Payload);
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default)
    {
        var list = messages as ICollection<OutboxMessage> ?? messages.ToList();
        if (list.Count == 0)
            return;

        foreach (var message in list)
            message.Payload = SensitiveDataMask.SanitizeJson(message.Payload);
        db.OutboxMessages.AddRange(list);
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

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Cross-tenant worker claim (§5.1). Open an explicit short transaction: SELECT FOR UPDATE
        // SKIP LOCKED acquires row locks; committing the transaction releases them so the lease
        // fields (written by SaveChangesAsync) become visible to other workers — the committed
        // lease is what prevents another publisher from re-claiming before expiry.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // IgnoreQueryFilters: drop the ambient tenant filter so this cross-tenant worker
        // sees ALL tenants' rows.  No tenant predicate in the SQL — intentional (cross-tenant).
        // All value parameters are bound ({0}…{2}) — never string-concatenated.
        var rows = await db.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages " +
                "WHERE published_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY occurred_at " +
                "LIMIT {1} " +
                "FOR UPDATE SKIP LOCKED",
                now,
                limit)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.LeaseOwner = leaseOwner;
            row.LeaseExpiresAt = leaseExpiresAt;
            row.PublishAttempts++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rows;
    }

    public Task MarkPublishedAsync(
        string tenantId,
        string messageId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        // ExecuteUpdateAsync issues a single UPDATE without loading the entity.
        // IgnoreQueryFilters: the publisher's DbContext may not be scoped to tenantId;
        // the WHERE predicate supplies explicit tenant scope (never rely on the ambient filter).
        return db.Set<OutboxMessage>()
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.MessageId == messageId)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(m => m.PublishedAt, publishedAt)
                    .SetProperty(m => m.LeaseOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);
    }

    public Task<bool> ExistsAsync(string tenantId, string messageId, CancellationToken ct = default)
        => db.Set<OutboxMessage>()
            .IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageId == messageId, ct);
}
