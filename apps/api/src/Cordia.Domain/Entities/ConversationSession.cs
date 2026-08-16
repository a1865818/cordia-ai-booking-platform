using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Represents a single conversation session scoped to a tenant. Carries the resolving
/// <see cref="ChannelEndpointId"/> and the ingress <see cref="CorrelationId"/> (spec-15 §5.4) so the
/// row links back to the endpoint that minted it and the correlation chain of its resolution; both
/// are nullable so rows created before those columns existed stay valid.
/// </summary>
public class ConversationSession : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public TenantChannel Channel { get; set; }

    public string? ChannelEndpointId { get; set; }

    public string? CorrelationId { get; set; }

    public ConsentState? ConsentState { get; set; }

    public string? ConsentPolicyVersion { get; set; }

    public string? JurisdictionPolicyId { get; set; }

    public bool? RecordingAllowed { get; set; }

    public DateTimeOffset? ConsentRecordedAt { get; set; }

    public string? CustomerRef { get; set; }

    /// <summary>Redacted snippet for conversation list rows; max 280 characters.</summary>
    public string? Preview { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public long Version { get; set; }
}
