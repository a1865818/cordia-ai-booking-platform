using MediatR;

namespace Cordia.Application.Handlers.Notifications.Reconcile;

/// <summary>
/// Command for §12.8 capability-ladder recovery of one notification row stuck in <c>Sending</c>
/// (spec 10 §6.2, §13.6).
/// </summary>
public sealed record ReconcileNotificationCommand(string TenantId, string NotificationId)
    : IRequest<ReconcileOutcome>;

/// <summary>
/// Result of a single reconcile attempt.
/// </summary>
public enum ReconcileOutcome
{
    /// <summary>No row found for (tenantId, notificationId).</summary>
    NotFound,

    /// <summary>Row is not in <c>Sending</c> — already settled (idempotent no-op).</summary>
    NotSending,

    /// <summary>
    /// Provider lookup-by-key confirmed acceptance; row settled <c>Sending→Accepted</c>
    /// with no resend (effectively-once — §13.6 branch (a)).
    /// </summary>
    AcceptedViaLookup,

    /// <summary>
    /// Provider resend accepted; provider supports send-idempotency so no duplicate message
    /// is possible (§13.6 branch (b)).
    /// </summary>
    AcceptedViaResend,

    /// <summary>
    /// Provider resend accepted, but provider supports neither send-idempotency nor lookup
    /// (FM#5 — §13.6 branch (c)). Duplicate message is a documented residual risk; a
    /// <c>notification.duplicate_risk</c> audit event was emitted.
    /// </summary>
    AcceptedViaResendDuplicateRisk,

    /// <summary>
    /// Provider returned a transient or permanent failure, or no adapter exists for the
    /// channel. Row settled to a failure state.
    /// </summary>
    Failed,
}
