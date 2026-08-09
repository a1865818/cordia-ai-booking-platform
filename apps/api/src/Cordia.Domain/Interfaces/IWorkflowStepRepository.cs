using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for workflow steps; version-token transitions use the §22.1 helper.</summary>
public interface IWorkflowStepRepository
{
    // NOTE: there is deliberately no plain `AddAsync` — the same reasoning as
    // IOperationWorkItemRepository, and for the same index.
    //
    // Every writer here is racing a redelivery or the step sweeper for the same operation, and
    // `uq_workflow_step_per_type` makes a losing INSERT raise 23505 — which aborts the WHOLE
    // ambient transaction. All eight creation paths were read-then-add, so a race did not merely
    // fail to reuse the winner's step: it took down whatever else that transaction was writing.
    // In Validate and ReserveCapacity that is the operation's own status transition, so a normal
    // redelivery rolled the operation back to its previous state instead of advancing it.
    //
    // GetOrAddAsync below lets the database absorb the conflict, so the transaction stays clean
    // and a lost race is the benign "reuse the winner's step" it always claimed to be.

    /// <summary>
    /// Returns the existing step for (tenant, operation, stepType), or inserts
    /// <paramref name="step"/> and returns it. Idempotent under concurrency: the insert is
    /// <c>ON CONFLICT DO NOTHING</c>, so a caller that loses the race reads the winner's row back
    /// instead of raising a constraint violation that would poison the ambient transaction.
    /// </summary>
    Task<WorkflowStep> GetOrAddAsync(WorkflowStep step, CancellationToken ct = default);

    Task<WorkflowStep?> GetAsync(string workflowStepId, CancellationToken ct = default);

    /// <summary>Loads the single workflow step for (tenant, operation, stepType) — the uq_workflow_step_per_type
    /// key — or null if none exists yet. AsNoTracking (state+version read; transitions go via TryTransitionStatusAsync).</summary>
    Task<WorkflowStep?> GetByOperationAndTypeAsync(string tenantId, string operationId, string stepType, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE (§6.2): transitions to <paramref name="next"/> only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = won race, 0 = lost race / wrong state → reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string workflowStepId,
        long expectedVersion,
        WorkflowStepStatus expectedStatus,
        WorkflowStepStatus next,
        CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> STALE NON-TERMINAL steps across ALL tenants for the
    /// step sweeper: status in (Pending, InProgress), updated before <paramref name="staleBefore"/>
    /// (so steps being actively worked are not swept), and ready to run (NotBefore null or ≤ now).
    /// Ordered by updated_at ascending (oldest first). AsNoTracking; cross-tenant (IgnoreQueryFilters).
    /// </summary>
    Task<IReadOnlyList<WorkflowStep>> ListNonTerminalAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <c>NotBefore</c> and <c>UpdatedAt</c> on the identified step WITHOUT bumping
    /// <c>Version</c> (spec 06 §15.2 durable schedule: the sweeper must not re-enqueue the step
    /// before the scheduled retry fires). Cross-tenant safe (IgnoreQueryFilters + tenant predicate).
    /// </summary>
    Task SetNotBeforeAsync(
        string tenantId,
        string workflowStepId,
        DateTimeOffset notBefore,
        CancellationToken ct = default);
}
