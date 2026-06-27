using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IWorkflowAttemptRepository"/> (Inv-15: rows never deleted,
/// AttemptNumber never reset; a single attempt's mutable fields update through its lifecycle per §6.2).</summary>
public class WorkflowAttemptRepository(ApplicationDbContext db) : IWorkflowAttemptRepository
{
    public async Task AddAsync(WorkflowAttempt attempt, CancellationToken ct = default)
    {
        db.WorkflowAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
    }

    public Task<WorkflowAttempt?> GetAsync(string attemptId, CancellationToken ct = default)
        => db.WorkflowAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == attemptId, ct);

    /// <summary>Callers may pass either the tracked instance they created or a fresh detached instance
    /// with the same AttemptId; EF excludes PK columns from the SET clause so identity fields remain immutable.
    /// If a DIFFERENT tracked instance with the same AttemptId is already in the change tracker
    /// (e.g. from a prior Finalize step in the same scope), it is detached before the new instance
    /// is attached — this handles the crash-after-provider reconcile path (T5).</summary>
    public async Task UpdateAsync(WorkflowAttempt attempt, CancellationToken ct = default)
    {
        // Detach any conflicting tracked entity with the same key so Update() can attach the new one.
        var tracked = db.ChangeTracker.Entries<WorkflowAttempt>()
            .FirstOrDefault(e => e.Entity.AttemptId == attempt.AttemptId
                              && !ReferenceEquals(e.Entity, attempt));
        if (tracked is not null)
            tracked.State = EntityState.Detached;

        db.WorkflowAttempts.Update(attempt);
        await db.SaveChangesAsync(ct);
    }

    public Task<WorkflowAttempt?> GetLatestForStepAsync(
        string tenantId,
        string operationId,
        string workflowStep,
        CancellationToken ct = default)
        => db.WorkflowAttempts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OperationId == operationId && a.WorkflowStep == workflowStep)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync(ct);
}
