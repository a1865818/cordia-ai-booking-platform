using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Notification delivery row with optimistic-concurrency version token.
/// PK (tenant_id, notification_id). FK to booking_operations ON DELETE RESTRICT.
/// </summary>
public class Notification : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string NotificationId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public NotificationEvent EventType { get; set; }

    public string RecipientRef { get; set; } = string.Empty;

    public string TemplateVersion { get; set; } = string.Empty;

    public int MaterialRevision { get; set; }

    public NotificationState State { get; set; }

    public string? ProviderIdempotencyKey { get; set; }

    public string? ProviderRef { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
