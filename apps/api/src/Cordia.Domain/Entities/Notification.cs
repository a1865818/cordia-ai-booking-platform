using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Notification delivery row with optimistic-concurrency version token.
/// PK (tenant_id, notification_id) = §22.1 unique logical-dedup constraint (Inv-9).
/// FK to booking_operations ON DELETE RESTRICT — enforced only when <see cref="OperationId"/> is
/// non-null: spec-11 waitlist offer/expiry events carry an <c>op_wl_*</c> id whose
/// booking_operations row is minted only at accept, so those rows store NULL here (the envelope's
/// operation id still keys the notification identity for dedup).
/// </summary>
public class Notification : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string NotificationId { get; set; } = string.Empty;

    public string? OperationId { get; set; }

    public NotificationEvent EventType { get; set; }

    public string RecipientRef { get; set; } = string.Empty;

    public string TemplateVersion { get; set; } = string.Empty;

    public int MaterialRevision { get; set; }

    public NotificationState State { get; set; }

    public NotificationChannel Channel { get; set; }

    public MessagePriority Priority { get; set; }

    public string? ProviderIdempotencyKey { get; set; }

    public string? ProviderRef { get; set; }

    public string? FailureCategory { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? NextEligibleAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }

    /// <summary>Guarded in-memory transition (§6.1): validates the edge then mutates State/FailureCategory/UpdatedAt.
    /// Throws <see cref="Exceptions.InvalidNotificationStateTransitionException"/> on an undefined edge.
    /// Persistence is done separately via the repository's version-guarded TryTransitionStateAsync;
    /// this method does NOT bump Version.</summary>
    public void TransitionTo(NotificationState next, string? failureCategory = null)
    {
        NotificationStateMachine.EnsureAllowed(State, next);
        State = next;
        UpdatedAt = DateTimeOffset.UtcNow;
        FailureCategory = failureCategory;
    }
}
