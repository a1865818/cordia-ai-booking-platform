using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Idempotency fence record keyed on (tenant_id, idempotency_key) (§13.3, Inv-3).</summary>
public class IdempotencyRecord : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public OperationTypeEnum OperationType { get; set; }

    /// <summary>Canonical request hash; one key is forever bound to exactly one hash (Inv-3).</summary>
    public string RequestHash { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public IdempotencyStatus ProcessingStatus { get; set; }

    /// <summary>Cached response snapshot stored as jsonb; may expire earlier than the record.</summary>
    public string? ResponseSnapshot { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Retention deadline: life of booking + dispute/audit window (§13.3).</summary>
    public DateTimeOffset RetentionDeadline { get; set; }
}
