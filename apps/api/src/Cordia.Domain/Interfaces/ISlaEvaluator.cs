namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for evaluating SLA thresholds on a single booking operation (spec 06 §8, T9).
/// Decouples the SLA evaluator hosted service in Infrastructure from MediatR/Application.
/// </summary>
public interface ISlaEvaluator
{
    /// <summary>
    /// Evaluates SLA thresholds for the given operation.  Emits pending-notification and/or
    /// escalation work-item + notification rows as needed.  Idempotent.
    /// </summary>
    Task EvaluateAsync(string tenantId, string operationId, CancellationToken ct = default);
}
