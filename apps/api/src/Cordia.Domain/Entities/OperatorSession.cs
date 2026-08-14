using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Issued console session. PK (tenant_id, session_id). Indexed on session_id for claims lookup.</summary>
public class OperatorSession : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string OperatorId { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? CorrelationId { get; set; }
}
