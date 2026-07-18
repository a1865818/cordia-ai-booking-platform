using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Domain.Entities;

/// <summary>Mutable operation work item with optimistic-concurrency version token.</summary>
public class OperationWorkItem : ITenantScoped, ICorrelatable
{
    public string TenantId { get; set; } = string.Empty;

    public string WorkItemId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public DateTimeOffset SlaDeadline { get; set; }

    public string? Assignee { get; set; }

    public string? Resolution { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }

    /// <summary>Ambient correlation id stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation id lineage stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CausationId { get; set; }
}
