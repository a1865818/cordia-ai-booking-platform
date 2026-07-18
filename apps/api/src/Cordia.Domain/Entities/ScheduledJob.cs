using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Domain.Entities;

/// <summary>Durable scheduled job (spec 05); job_type stored as TEXT (open value set).</summary>
public class ScheduledJob : ITenantScoped, ICorrelatable
{
    public string TenantId { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    /// <summary>Open-set job type label (text, not enum — spec §5.0 open-set rule).</summary>
    public string JobType { get; set; } = string.Empty;

    public DateTimeOffset RunAfter { get; set; }

    public string? WorkflowStepId { get; set; }

    public string DestinationQueue { get; set; } = string.Empty;

    public ScheduledJobStatus Status { get; set; }

    public int Attempt { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Job payload stored as jsonb.</summary>
    public string Payload { get; set; } = "{}";

    public string? OperationId { get; set; }

    /// <summary>Ambient correlation id stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation id lineage stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CausationId { get; set; }
}
