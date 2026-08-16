using System.Text.Json.Serialization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Handoffs.RequestHandoff;

/// <summary>
/// Escalates a live session to a human agent (spec 03 §4.3,
/// <c>request_human_handoff</c> — BOUNDED_SIDE_EFFECT, dedup-keyed).
///
/// Idempotent per <see cref="HandoffIdempotencyKey"/>: the agent derives it
/// deterministically from session + reason, so a retried or reconnected
/// escalation returns the EXISTING work item with
/// <c>status = "existing"</c> instead of queueing the caller twice (Inv-5/Inv-9).
/// </summary>
public sealed record RequestHandoffCommand(
    string TenantId,
    string SessionId,
    string ReasonCode,
    string HandoffIdempotencyKey,
    string? RecommendedAction) : IRequest<HandoffResult>;

/// <summary>
/// Outcome of an escalation (snake_case wire shape — the Python
/// <c>HandoffResult</c> contract parses these keys and rejects unknown ones).
/// </summary>
public sealed record HandoffResult(
    [property: JsonPropertyName("work_item_id")] string WorkItemId,
    [property: JsonPropertyName("queue")] string Queue,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Structural rules for an escalation request.</summary>
public sealed class RequestHandoffValidator : AbstractValidator<RequestHandoffCommand>
{
    public RequestHandoffValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty().MaximumLength(128);
        // The dedup key is the whole idempotency guarantee; an empty one would
        // silently make every retry a new work item.
        RuleFor(x => x.HandoffIdempotencyKey).NotEmpty().MaximumLength(256);
        RuleFor(x => x.RecommendedAction).MaximumLength(512);
    }
}
