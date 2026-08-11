using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IOperationWorkItemRepository"/>.</summary>
public class OperationWorkItemRepository(ApplicationDbContext db) : IOperationWorkItemRepository
{
    public Task<int> TryUpdateStateAsync(
        string tenantId,
        string workItemId,
        long expectedVersion,
        string expectedState,
        string nextState,
        string? assignee,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<OperationWorkItem>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.WorkItemId == workItemId && e.State == expectedState,
            s => s
                .SetProperty(x => x.State, nextState)
                .SetProperty(x => x.Assignee, assignee),
            ct);

    public Task<OperationWorkItem?> GetOpenByOperationAsync(
        string tenantId, string operationId, CancellationToken ct = default)
        => db.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(w => w.TenantId == tenantId && w.OperationId == operationId && w.State == "open")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT … ON CONFLICT (tenant_id, operation_id) WHERE state = 'open' DO NOTHING</c>
    /// to match the partial unique index <c>uq_open_work_item_per_operation</c>. Returns 1 when
    /// the row was inserted; 0 when another worker already holds the open work item (benign no-op).
    /// </remarks>
    public async Task<bool> TryAddOpenAsync(OperationWorkItem workItem, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.operations_work_items
                (tenant_id, work_item_id, operation_id, reason, state,
                 sla_deadline, assignee, resolution, created_at, version)
            VALUES
                ({workItem.TenantId}, {workItem.WorkItemId}, {workItem.OperationId},
                 {workItem.Reason}, {workItem.State},
                 {workItem.SlaDeadline}, {workItem.Assignee}, {workItem.Resolution},
                 {workItem.CreatedAt}, {workItem.Version})
            ON CONFLICT (tenant_id, operation_id) WHERE state = 'open' DO NOTHING
            """,
            ct);

        return rows == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationWorkItem>> ListOpenAsync(
        string tenantId, int limit, CancellationToken ct = default)
        // IgnoreQueryFilters with an EXPLICIT tenant predicate, matching how every other
        // tenant-parameterised read in this project is written: the scope is stated in the query
        // rather than inherited from ambient state, so it is visible and testable here (Inv-13).
        => await db.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(w => w.TenantId == tenantId && w.State == "open")
            .OrderBy(w => w.SlaDeadline)
            .ThenBy(w => w.WorkItemId)
            .Take(limit)
            .ToListAsync(ct);

    public Task<int> CountOpenAsync(CancellationToken ct = default)
        => db.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(w => w.State == "open", ct);

    public async Task<double> OldestOpenAgeSecondsAsync(CancellationToken ct = default)
    {
        var oldest = await db.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(w => w.State == "open")
            .MinAsync(w => (DateTimeOffset?)w.CreatedAt, ct);

        return oldest.HasValue
            ? (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds
            : 0d;
    }
}
