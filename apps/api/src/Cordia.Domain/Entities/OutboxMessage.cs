using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Transactional outbox message (Inv-4); claimed via FOR UPDATE SKIP LOCKED.</summary>
public class OutboxMessage : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public string? OperationId { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string? CausationId { get; set; }

    /// <summary>Sanitised event payload; no secrets / no PAN (Inv-12). Stored as jsonb.</summary>
    public string Payload { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int PublishAttempts { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
