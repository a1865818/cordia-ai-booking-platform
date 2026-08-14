using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Agent guardrail toggles for a tenant. PK tenant_id. Locked ids g1 and g4 are enforced at the handler layer.</summary>
public class AgentGuardrails : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>jsonb map of guardrail id → bool.</summary>
    public string TogglesJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
}
