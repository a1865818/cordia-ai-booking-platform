using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Append-only audit event row (spec 14 / spec 12 §5.1 line 86).
/// PK (tenant_id, event_id). No FK — all refs are loose. No version token.
/// Insert-only: the repository exposes no update or delete methods.
/// </summary>
public class AuditEvent : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string SubjectRef { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string? OperationId { get; set; }

    /// <summary>Sanitized metadata jsonb (no PAN/CVV). Stored as jsonb in PostgreSQL.</summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>Defaults to now() in the database.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
