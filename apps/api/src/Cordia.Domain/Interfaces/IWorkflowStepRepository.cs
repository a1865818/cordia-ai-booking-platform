using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for workflow steps; version-token transitions use the §22.1 helper.</summary>
public interface IWorkflowStepRepository
{
    Task AddAsync(WorkflowStep step, CancellationToken ct = default);

    Task<WorkflowStep?> GetAsync(string workflowStepId, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE: transitions status from <paramref name="expectedVersion"/>
    /// to <paramref name="next"/>. Returns rows-affected (1 = won race, 0 = lost race → reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string workflowStepId,
        long expectedVersion,
        WorkflowStepStatus next,
        CancellationToken ct = default);
}
