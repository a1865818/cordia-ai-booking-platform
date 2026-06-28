using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IOperationWorkItemRepository"/>.</summary>
public class OperationWorkItemRepository(ApplicationDbContext db) : IOperationWorkItemRepository
{
    public async Task AddAsync(OperationWorkItem workItem, CancellationToken ct = default)
    {
        db.OperationWorkItems.Add(workItem);
        await db.SaveChangesAsync(ct);
    }

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
}
