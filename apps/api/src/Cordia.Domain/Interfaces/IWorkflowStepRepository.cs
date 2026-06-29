using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for workflow steps; version-token transitions use the §22.1 helper.</summary>
public interface IWorkflowStepRepository
{
    Task AddAsync(WorkflowStep step, CancellationToken ct = default);

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
