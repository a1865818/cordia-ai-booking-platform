using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IHandoffWorkItemRepository"/>.</summary>
public class HandoffWorkItemRepository(ApplicationDbContext db) : IHandoffWorkItemRepository
{
    public Task<HandoffWorkItem?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default)
        => db.HandoffWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(w => w.TenantId == tenantId && w.HandoffIdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Raw INSERT … ON CONFLICT rather than Add + SaveChanges, so losing the race
    /// is a 0-row result instead of a <c>DbUpdateException</c> the caller has to
    /// unpick. Mirrors <c>OperationWorkItemRepository.TryAddOpenAsync</c> and
    /// <c>InboxRepository.TryAdmitAsync</c>.
    /// </remarks>
    public async Task<bool> TryAddAsync(HandoffWorkItem workItem, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.handoff_work_items
                (tenant_id, work_item_id, session_id, reason_code,
                 handoff_idempotency_key, queue, state, recommended_action,
                 assignee, created_at, version, correlation_id, causation_id)
            VALUES
                ({workItem.TenantId}, {workItem.WorkItemId}, {workItem.SessionId},
                 {workItem.ReasonCode}, {workItem.HandoffIdempotencyKey},
                 {workItem.Queue}, {workItem.State}, {workItem.RecommendedAction},
                 {workItem.Assignee}, {workItem.CreatedAt}, {workItem.Version},
                 {workItem.CorrelationId}, {workItem.CausationId})
            ON CONFLICT (tenant_id, handoff_idempotency_key) DO NOTHING
            """,
            ct);

        return rows == 1;
    }
}
