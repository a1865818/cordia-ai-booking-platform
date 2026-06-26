using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Append-only workflow attempt log row (Inv-15: no update after insert).</summary>
public class WorkflowAttempt : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string AttemptId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string WorkflowStep { get; set; } = string.Empty;

    public string? ProviderIdempotencyKey { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string ProviderAccount { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public int AttemptNumber { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public AttemptOutcome Outcome { get; set; }

    public RetryClassification? RetryClassification { get; set; }

    public string? ProviderRef { get; set; }

    // jsonb (not null): default to a valid empty JSON object — '' is not valid jsonb.
    public string RequestMeta { get; set; } = "{}";

    public string ResponseMeta { get; set; } = "{}";

    public string? ErrorCode { get; set; }

    public DateTimeOffset? NextEligibleAt { get; set; }

    public string? Reconciliation { get; set; }
}
