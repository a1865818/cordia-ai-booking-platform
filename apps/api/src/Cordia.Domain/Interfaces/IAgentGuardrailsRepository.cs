using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Read/write access to the tenant's agent guardrail toggles.</summary>
public interface IAgentGuardrailsRepository
{
    Task<AgentGuardrails?> GetAsync(string tenantId, CancellationToken ct = default);

    Task UpsertAsync(AgentGuardrails guardrails, CancellationToken ct = default);
}
