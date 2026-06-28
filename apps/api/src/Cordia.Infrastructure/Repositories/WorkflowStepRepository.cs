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

    public Task<WorkflowStep?> GetByOperationAndTypeAsync(
        string tenantId,
        string operationId,
        string stepType,
        CancellationToken ct = default)
        => db.WorkflowSteps
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OperationId == operationId && s.StepType == stepType, ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string workflowStepId,
        long expectedVersion,
        WorkflowStepStatus expectedStatus,
        WorkflowStepStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WorkflowStep>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.WorkflowStepId == workflowStepId && e.Status == expectedStatus,
            s => s
                .SetProperty(x => x.Status, next)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);

    public Task SetNotBeforeAsync(
        string tenantId,
        string workflowStepId,
        DateTimeOffset notBefore,
        CancellationToken ct = default)
        // NOT a version-token update — intentionally does NOT bump Version (§15.2 spec note).
        => db.WorkflowSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.WorkflowStepId == workflowStepId)
            .ExecuteUpdateAsync(b => b
                .SetProperty(x => x.NotBefore, notBefore)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

    public async Task<IReadOnlyList<WorkflowStep>> ListNonTerminalAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        // Cross-tenant sweep (T6, §5.1): IgnoreQueryFilters bypasses the ambient tenant filter.
        // Returns stale non-terminal steps: status Pending or InProgress, untouched since
        // staleBefore (actively-worked steps have UpdatedAt ≥ staleBefore and are excluded),
        // and ready to run (NotBefore null or in the past). Oldest-first so the sweeper
        // always makes forward progress from the most-overdue steps.
        var results = await db.WorkflowSteps
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s =>
                (s.Status == WorkflowStepStatus.Pending || s.Status == WorkflowStepStatus.InProgress)
                && s.UpdatedAt < staleBefore
                && (s.NotBefore == null || s.NotBefore <= now))
            .OrderBy(s => s.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return results;
    }
}
