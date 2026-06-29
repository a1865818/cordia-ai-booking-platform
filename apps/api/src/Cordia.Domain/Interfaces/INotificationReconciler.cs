namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for reconciling a single notification row stuck in <c>Sending</c>
/// (spec 10 §12.8 / §13.6 capability-ladder recovery).
///
/// Decouples the <c>ReconcileNotificationsHostedService</c> in Infrastructure from
/// MediatR / Application, mirroring <see cref="ISlaEvaluator"/> for SLA evaluation.
/// </summary>
public interface INotificationReconciler
{
    /// <summary>
    /// Applies the §13.6 capability-ladder recovery for one notification stuck in <c>Sending</c>:
    /// lookup-by-key → idempotent-resend → documented-duplicate (best-effort) resend.
    /// Idempotent: if the row is no longer in <c>Sending</c> the call is a no-op.
    /// </summary>
    Task ReconcileAsync(string tenantId, string notificationId, CancellationToken ct = default);
}
