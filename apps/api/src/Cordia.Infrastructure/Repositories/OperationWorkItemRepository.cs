using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;

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
        string nextState,
        string? assignee,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<OperationWorkItem>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.WorkItemId == workItemId,
            s => s
                .SetProperty(x => x.State, nextState)
                .SetProperty(x => x.Assignee, assignee),
            ct);
}
