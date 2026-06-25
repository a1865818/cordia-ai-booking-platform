using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IWorkflowStepRepository"/>.</summary>
public class WorkflowStepRepository(ApplicationDbContext db) : IWorkflowStepRepository
{
    public async Task AddAsync(WorkflowStep step, CancellationToken ct = default)
    {
        db.WorkflowSteps.Add(step);
        await db.SaveChangesAsync(ct);
    }

    public Task<WorkflowStep?> GetAsync(string workflowStepId, CancellationToken ct = default)
        => db.WorkflowSteps
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowStepId == workflowStepId, ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string workflowStepId,
        long expectedVersion,
        WorkflowStepStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WorkflowStep>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.WorkflowStepId == workflowStepId,
            s => s
                .SetProperty(x => x.Status, next)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);
}
