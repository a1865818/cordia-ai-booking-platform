using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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

    /// <summary>
    /// One statement, one round trip: <c>UPDATE workflow_attempts SET outcome = 'dispatching'
    /// WHERE attempt_id = … AND outcome = 'planned' AND EXISTS (SELECT 1 FROM booking_operations
    /// WHERE … AND status = 'finalizing')</c>.
    ///
    /// <para>Doing it as a single conditional statement is the whole point — a read followed by a
    /// write would reintroduce the very gap it exists to close. The <c>outcome = 'planned'</c>
    /// clause also makes it idempotent: a redelivery that finds the attempt already Dispatching
    /// gets 0 rows rather than re-arming a dispatch.</para>
    ///
    /// <para><b>It deliberately does NOT test <c>cancellation_requested_at</c>.</b> A pending
    /// cancellation is not on its own a reason to skip the provider call. After a
    /// <c>TransientFailure</c> attempt the PREVIOUS call may already have taken effect, and the
    /// only way to find out is to call again under the same idempotency key and read the
    /// <c>AlreadyExists</c> reference back — which is what gives Compensate something it can
    /// actually cancel. FM#13 encodes exactly that: it fast-paths a cancellation to
    /// CompensationRequired only when the attempt is <c>null</c> or <c>Planned</c>, and routes
    /// everything else through create-then-compensate. Guarding on the flag here would refuse
    /// those dispatches and strand the operation without a reference.</para>
    ///
    /// <para>The status clause is what closes #51 anyway: the cancel worker's CAS moves the
    /// operation off <c>Finalizing</c>, and once it has, this statement matches nothing.</para>
    /// </summary>
    public Task<int> TryMarkDispatchingAsync(
        string tenantId, string operationId, string attemptId, CancellationToken ct = default)
        => db.WorkflowAttempts
            .Where(a => a.TenantId == tenantId
                     && a.AttemptId == attemptId
                     // The attempt must belong to the operation whose status is being asserted.
                     // Without this the EXISTS below is checked against one operation while the
                     // UPDATE arms another's attempt, so a caller that ever passed a mismatched
                     // pair would dispatch using one operation's idempotency key and request hash
                     // on behalf of a different operation. Today's single call site always passes
                     // a matching pair; a conditional write should not depend on that.
                     && a.OperationId == operationId
                     && a.Outcome == AttemptOutcome.Planned
                     && db.BookingOperations.Any(o =>
                            o.TenantId == tenantId
                         && o.OperationId == operationId
                         && o.Status == OperationStatus.Finalizing))
            .ExecuteUpdateAsync(
                b => b.SetProperty(a => a.Outcome, AttemptOutcome.Dispatching), ct);

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
