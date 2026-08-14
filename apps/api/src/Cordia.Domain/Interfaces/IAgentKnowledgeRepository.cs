using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Access to agent knowledge-base entries.</summary>
public interface IAgentKnowledgeRepository
{
    Task<IReadOnlyList<AgentKnowledgeEntry>> ListAsync(string tenantId, CancellationToken ct = default);

    Task<AgentKnowledgeEntry?> GetAsync(string tenantId, string entryId, CancellationToken ct = default);

    Task AddAsync(AgentKnowledgeEntry entry, CancellationToken ct = default);

    Task UpdateAsync(AgentKnowledgeEntry entry, CancellationToken ct = default);
}
