using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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
        string tenantId,
        string consumerName,
        string messageId,
        CancellationToken ct = default)
        // Explicit tenant predicate + IgnoreQueryFilters: the caller (e.g. the replay handler) may
        // not be request-scoped to this tenant, so the authoritative (tenant, consumer, message)
        // key must be enforced in the predicate rather than relying on the ambient query filter.
        => db.ConsumerInbox
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ConsumerName == consumerName && e.MessageId == messageId,
                ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT … ON CONFLICT (consumer_name, tenant_id, message_id) DO NOTHING</c> so
    /// that a duplicate delivery does NOT abort the surrounding transaction (unlike a bare unique
    /// violation).  All enum columns receive explicit pg-enum casts to avoid "unknown type" errors
    /// when the Npgsql type-mapper has not yet seen the enum on this connection.
    /// Returns 1 (won / admitted) or 0 (lost / already present).
    /// </remarks>
    public async Task<bool> TryAdmitAsync(ConsumerInbox inbox, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.consumer_inbox
                (consumer_name, tenant_id, message_id, correlation_id, operation_id,
                 outcome, admitted_at)
            VALUES
                ({inbox.ConsumerName}, {inbox.TenantId}, {inbox.MessageId},
                 {inbox.CorrelationId}, {inbox.OperationId},
                 {PgEnum.Label(InboxOutcome.Admitted)}::inbox_outcome_enum,
                 {inbox.AdmittedAt})
            ON CONFLICT (consumer_name, tenant_id, message_id) DO NOTHING
            """,
            ct);

        return rows == 1;
    }
}
