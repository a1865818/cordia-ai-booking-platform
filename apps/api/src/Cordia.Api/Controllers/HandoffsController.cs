using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Handoffs.RequestHandoff;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Human-handoff endpoint backing the agent's <c>request_human_handoff</c> tool
/// (spec 03 §4.3 — BOUNDED_SIDE_EFFECT, dedup-keyed).
///
/// POST /api/handoffs → 200 (created or existing) | 400
///
/// This route used to 405. The agent degraded gracefully — <c>human_handoff</c>
/// still set its terminal state — but "the escalation went nowhere" is a
/// tolerable outcome on a text transcript and not one on a live call, where the
/// alternative to a human is hanging up on the caller.
///
/// Idempotent per <c>handoff_idempotency_key</c>: the agent derives it
/// deterministically from session + reason code, so a retry or a reconnect
/// returns the same work item with <c>status = "existing"</c> instead of
/// queueing the same caller twice (Inv-5/Inv-9).
/// </summary>
[ApiController]
[Route("api/handoffs")]
public class HandoffsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Escalates a session to a human agent, or returns the existing escalation.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(HandoffResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HandoffResult>> RequestHandoff(
        [FromBody] HandoffRequestBody? body,
        CancellationToken ct)
    {
        // With SuppressImplicitRequiredAttributeForNonNullableReferenceTypes on,
        // a missing/null JSON body binds to null rather than auto-400.
        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON handoff body is required.");

        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new RequestHandoffCommand(
                tenant.TenantId,
                body.SessionId ?? string.Empty,
                body.ReasonCode ?? string.Empty,
                body.HandoffIdempotencyKey ?? string.Empty,
                body.RecommendedAction),
            ct);

        return Ok(result);
    }
}

/// <summary>Body for POST /api/handoffs — mirrors the Python <c>HandoffRequest</c>.</summary>
public sealed record HandoffRequestBody(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("reason_code")] string? ReasonCode,
    [property: JsonPropertyName("handoff_idempotency_key")] string? HandoffIdempotencyKey,
    [property: JsonPropertyName("recommended_action")] string? RecommendedAction)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public HandoffRequestBody() : this(null, null, null, null) { }
}
