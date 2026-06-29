using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for workflow attempts. Attempt rows are never deleted and AttemptNumber is never
/// reset (Inv-15: each retry is a new row); a single attempt's outcome/timing/result fields ARE updated
/// through its lifecycle (§6.2).</summary>
public interface IWorkflowAttemptRepository
{
    Task AddAsync(WorkflowAttempt attempt, CancellationToken ct = default);

    Task<WorkflowAttempt?> GetAsync(string attemptId, CancellationToken ct = default);

    /// <summary>Persists outcome/timing/result changes for an existing attempt (identity fields
    /// AttemptId/AttemptNumber/RequestHash/ProviderIdempotencyKey/ProviderName/ProviderAccount are immutable;
    /// only Outcome/StartedAt/CompletedAt/ProviderRef/RetryClassification/ResponseMeta/RequestMeta/ErrorCode/
    /// NextEligibleAt/Reconciliation change). §6.2.</summary>
    Task UpdateAsync(WorkflowAttempt attempt, CancellationToken ct = default);

    /// <summary>Returns the highest-AttemptNumber attempt for (tenant, operationId, workflowStep), or null —
    /// used by recovery/reconcile to inspect the last attempt's outcome (§6.2, §16). AsNoTracking.</summary>
    Task<WorkflowAttempt?> GetLatestForStepAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default);
}
