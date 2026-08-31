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

    /// <summary>
    /// Moves an attempt <c>Planned → Dispatching</c> ONLY while its operation is still
    /// <c>Finalizing</c>. Returns rows affected: 0 means the dispatch must be abandoned (#51).
    ///
    /// <para><b>Why this is not <see cref="UpdateAsync"/>.</b> Marking Dispatching is the last
    /// write before an irreversible external call, so it is the natural place to re-check that the
    /// call is still wanted. <c>UpdateAsync</c> returns <c>Task</c>, so a caller cannot learn that
    /// the world moved underneath it — which is exactly why the remedy #51 proposed (bumping a
    /// version there) could not work. This returns a count, so the caller can act on it.</para>
    ///
    /// <para><b>The guard is on <c>booking_operations</c>, not on the attempt row.</b>
    /// <c>workflow_attempts</c> has no version column (spec 12 §78), and the fact worth checking
    /// lives on the operation anyway: whether it is still the operation this dispatch was planned
    /// for. A pending cancellation is deliberately NOT part of the test — see the implementation
    /// for why refusing those dispatches would strand the operation.</para>
    /// </summary>
    Task<int> TryMarkDispatchingAsync(
        string tenantId, string operationId, string attemptId, CancellationToken ct = default);

    /// <summary>Returns the highest-AttemptNumber attempt for (tenant, operationId, workflowStep), or null —
    /// used by recovery/reconcile to inspect the last attempt's outcome (§6.2, §16). AsNoTracking.</summary>
    Task<WorkflowAttempt?> GetLatestForStepAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default);
}
