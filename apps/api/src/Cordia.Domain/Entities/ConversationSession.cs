using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Represents a single conversation session scoped to a tenant.</summary>
public class ConversationSession : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public TenantChannel Channel { get; set; }

    public ConsentState? ConsentState { get; set; }

    public string? ConsentPolicyVersion { get; set; }

    public string? JurisdictionPolicyId { get; set; }

    public bool? RecordingAllowed { get; set; }

    public DateTimeOffset? ConsentRecordedAt { get; set; }

    public string? CustomerRef { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public long Version { get; set; }
}
