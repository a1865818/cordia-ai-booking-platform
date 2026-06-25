using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Consumer-side inbox for at-least-once deduplication (Inv-5).
/// PK is (consumer_name, tenant_id, message_id) — consumer FIRST per spec §5.4.</summary>
public class ConsumerInbox : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string ConsumerName { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string? OperationId { get; set; }

    public InboxOutcome Outcome { get; set; }

    public DateTimeOffset AdmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
