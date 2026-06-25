using Cordia.Domain.Enums;

namespace Cordia.Domain.Entities;

/// <summary>
/// Cross-tenant ingress resolver row. PK (tenant_id, channel_endpoint_id).
/// Deliberately does NOT implement ITenantScoped — no global query filter is applied.
/// Unique partial index on (channel, signal_type, signal_value) WHERE status = 'active' (Inv-13).
/// Optimistic-concurrency version token (§22.1).
/// </summary>
public class TenantChannelEndpoint
{
    public string TenantId { get; set; } = string.Empty;

    public string ChannelEndpointId { get; set; } = string.Empty;

    public TenantChannel Channel { get; set; }

    public SignalType SignalType { get; set; }

    public string SignalValue { get; set; } = string.Empty;

    public string ConfigurationVersion { get; set; } = string.Empty;

    public string PolicyBundleId { get; set; } = string.Empty;

    public EndpointStatus Status { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
