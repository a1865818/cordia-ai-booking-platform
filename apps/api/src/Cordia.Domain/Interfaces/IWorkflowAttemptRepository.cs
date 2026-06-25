using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for workflow attempts (append-only log, Inv-15).</summary>
public interface IWorkflowAttemptRepository
{
    // Append-only (Inv-15): no Update method is intentional.
    Task AddAsync(WorkflowAttempt attempt, CancellationToken ct = default);

    Task<WorkflowAttempt?> GetAsync(string attemptId, CancellationToken ct = default);
}
