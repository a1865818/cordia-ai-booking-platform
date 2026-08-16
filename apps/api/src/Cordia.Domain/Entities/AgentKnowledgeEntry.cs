using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Agent knowledge-base entry. PK (tenant_id, entry_id).</summary>
public class AgentKnowledgeEntry : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string EntryId { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public string? Answer { get; set; }

    public KnowledgeStatus Status { get; set; } = KnowledgeStatus.NeedsAnswer;

    /// <summary><c>manual</c> or <c>handoff</c>.</summary>
    public string Source { get; set; } = "manual";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
