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

    /// <summary>
    /// Persists all non-key columns of <paramref name="attempt"/> via <c>db.Update()</c>.
    /// Callers MUST pass a fully-loaded attempt instance (e.g. the one they originally created
    /// or the result of <see cref="GetLatestForStepAsync"/> / <see cref="GetAsync"/>); passing a
    /// partially-populated detached instance will silently null out any column not set on it.
    /// If a DIFFERENT tracked instance with the same AttemptId is already in the change tracker
    /// (e.g. from a prior Finalize step in the same scope), it is detached before the supplied
    /// instance is attached — this handles the crash-after-provider reconcile path (T5).
    /// </summary>
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
