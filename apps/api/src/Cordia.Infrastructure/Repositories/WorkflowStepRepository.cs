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
    /// <inheritdoc/>
    public async Task<WorkflowStep> GetOrAddAsync(WorkflowStep step, CancellationToken ct = default)
    {
        var existing = await GetByOperationAndTypeAsync(step.TenantId, step.OperationId, step.StepType, ct);
        if (existing is not null)
            return existing;

        // ON CONFLICT DO NOTHING, not catch-the-violation — the same primitive and the same reason
        // as CapacitySlotRepository.TryAddAsync, OperationWorkItemRepository.TryAddOpenAsync and
        // IdempotencyRepository.ElectCreatorAsync.
        //
        // Catching 23505 cannot work here: every caller runs inside an ambient transaction, and
        // PostgreSQL aborts the WHOLE transaction on a constraint violation. By the time the
        // exception surfaced the transaction was already poisoned, so the "lost the race, read the
        // winner back" recovery — and the COMMIT itself — would fail with `current transaction is
        // aborted`, taking down the operation transition and outbox rows written alongside it.
        //
        // Letting the database absorb the conflict means no exception is ever raised, the
        // transaction stays clean, and the re-read below really can return the winner's row.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.workflow_steps
                (tenant_id, workflow_step_id, operation_id, step_type, status,
                 provider_idempotency_key, attempt_count, not_before,
                 created_at, updated_at, version)
            VALUES
                ({step.TenantId}, {step.WorkflowStepId}, {step.OperationId}, {step.StepType},
                 {PgEnum.Label(step.Status)}::workflow_step_status_enum,
                 {step.ProviderIdempotencyKey}, {step.AttemptCount}, {step.NotBefore},
                 {step.CreatedAt}, {step.UpdatedAt}, {step.Version})
            ON CONFLICT (tenant_id, operation_id, step_type) DO NOTHING
            """,
            ct);

        if (rows == 1)
            return step;

        // Lost the race. Under READ COMMITTED the conflicting insert has committed by the time
        // DO NOTHING reports zero rows (it blocks on an uncommitted one, and proceeds if that
        // writer rolls back), so the winner's row is visible to this read.
        //
        // THROWS if it is not, rather than falling back to the candidate — the same disposition as
        // GuestContactRepository, which handles the identical "conflicted but the winner could not
        // be read" case. Returning the candidate would hand back a workflow_step_id that exists
        // nowhere, and callers cannot tell: FinalizeBookingCommandHandler would make the external
        // provider call, its version-guarded transition would affect zero rows and read as a benign
        // lost race, and the retry scheduler would schedule against a step id that does not exist —
        // a booking created at the provider with the operation stranded in Finalizing. The old
        // read-then-add raised 23505 loudly here; this must not be quieter than what it replaced.
        return await GetByOperationAndTypeAsync(step.TenantId, step.OperationId, step.StepType, ct)
               ?? throw new InvalidOperationException(
                   $"Workflow step insert for operation '{step.OperationId}' (step '{step.StepType}', "
                   + $"tenant '{step.TenantId}') conflicted but the winning row could not be read.");
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
