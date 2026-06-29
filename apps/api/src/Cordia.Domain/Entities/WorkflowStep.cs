using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Mutable workflow step row with optimistic-concurrency version token.</summary>
public class WorkflowStep : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string WorkflowStepId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string StepType { get; set; } = string.Empty;

    public WorkflowStepStatus Status { get; set; }

    public string? ProviderIdempotencyKey { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? NotBefore { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
