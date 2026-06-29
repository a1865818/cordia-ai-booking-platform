using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Transport-level dead-letter store (D10); never overwrites booking_operations.status (Inv-14).</summary>
public class DeadLetterMessage : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public string ConsumerName { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    /// <summary>Sanitised payload stored as jsonb; no secrets / no PAN (Inv-12).</summary>
    public string Payload { get; set; } = "{}";

    public string ErrorClass { get; set; } = string.Empty;

    /// <summary>JSON array of attempt records, stored as jsonb.</summary>
    public string AttemptHistory { get; set; } = "[]";

    public DateTimeOffset FirstFailedAt { get; set; }

    public DateTimeOffset DeadLetteredAt { get; set; }

    public DlqReplayState ReplayState { get; set; }
}
