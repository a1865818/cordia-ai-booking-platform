using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Domain.Entities;

/// <summary>
/// A request to put a live session in front of a human agent (spec 03 §4.3,
/// <c>request_human_handoff</c>).
///
/// Distinct from <see cref="OperationWorkItem"/>, which is keyed on an
/// operation and exists to chase a booking mutation that stalled. A handoff is
/// keyed on the SESSION and can be raised with no operation at all — the
/// common case is a caller the agent cannot help, where nothing has been
/// submitted and nothing will be.
///
/// Idempotency (Inv-5/Inv-9): the agent derives
/// <see cref="HandoffIdempotencyKey"/> deterministically from
/// session + reason, so a retried escalation returns the existing work item
/// rather than queueing the same caller twice.
/// </summary>
public class HandoffWorkItem : ITenantScoped, ICorrelatable
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Stable identifier returned to the agent as <c>work_item_id</c>.</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>The dialog session being escalated.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Structured reason code, e.g. <c>verification_failed</c>.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Deterministic dedup key; unique per tenant (Inv-5/Inv-9).</summary>
    public string HandoffIdempotencyKey { get; set; } = string.Empty;

    /// <summary>Queue the item was routed to.</summary>
    public string Queue { get; set; } = string.Empty;

    /// <summary>Lifecycle state: <c>open</c>, <c>assigned</c>, or <c>resolved</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Optional machine hint for the human agent's first action.</summary>
    public string? RecommendedAction { get; set; }

    /// <summary>Human agent the item is assigned to, once picked up.</summary>
    public string? Assignee { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }

    /// <summary>Ambient correlation id stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation id lineage stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CausationId { get; set; }
}
