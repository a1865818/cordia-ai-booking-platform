using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Preorder.ManualResolution;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Operator-facing resolution endpoints for a pre-order saga parked in <c>ManualReview</c> (spec 11
/// §21.4). A pre-order must not linger after a failed/cancelled booking unless an operator AND the
/// customer explicitly convert it to a standalone order — or the operator cancels it.
///
/// POST /api/preorder/sagas/{sagaId}/convert-standalone → 200 | 400 | 404 | 409
/// POST /api/preorder/sagas/{sagaId}/cancel             → 200 | 400 | 404 | 409
///
/// Thin by design: resolves tenant (X-Tenant-Id) + operator (X-Actor) + reason code (X-Reason-Code),
/// sends a MediatR command, and maps typed domain exceptions via the GlobalExceptionHandler (saga not
/// found → 404, illegal transition → 409, missing grant → 400, invalid/expired customer grant → 403).
/// The customer half of §21.4 is a real spec-08 verification grant consumed by the convert handler.
/// Spec 14 §24.5 fail-closed (gate decision 2026-07-10): X-Actor and X-Reason-Code are MANDATORY —
/// missing values are rejected by the validators/operator behavior; staff RBAC over which operators
/// may act is the §12 open decision, so the operator actor is recorded (audited) but not yet
/// role-gated.
/// </summary>
[ApiController]
[Route("api/preorder")]
public class PreorderController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>Converts a parked pre-order to a standalone order (operator + a customer verification grant).</summary>
    [HttpPost("sagas/{sagaId}/convert-standalone")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConvertStandalone(
        string sagaId,
        [FromBody] ConvertStandaloneRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new ConvertPreorderToStandaloneCommand(
            tenant.TenantId, sagaId, resolvedActor, body.GrantId ?? string.Empty,
            reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { saga_id = sagaId, status = "converted_to_standalone" });
    }

    /// <summary>Cancels a parked pre-order (operator resolution — the §21.4 "or cancel" path).</summary>
    [HttpPost("sagas/{sagaId}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string sagaId,
        [FromBody] CancelPreorderRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new CancelPreorderManualReviewCommand(
            tenant.TenantId, sagaId, resolvedActor, body?.Reason,
            reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { saga_id = sagaId, status = "cancelled" });
    }
}

/// <summary>Body for POST /api/preorder/sagas/{sagaId}/convert-standalone. The grant is the customer's
/// single-use spec-08 verification grant evidencing agreement to the standalone conversion (§21.4).</summary>
public sealed record ConvertStandaloneRequest(
    [property: JsonPropertyName("grant_id")] string? GrantId)
{
    public ConvertStandaloneRequest() : this((string?)null) { }
}

/// <summary>Body for POST /api/preorder/sagas/{sagaId}/cancel.</summary>
public sealed record CancelPreorderRequest(
    [property: JsonPropertyName("reason")] string? Reason)
{
    public CancelPreorderRequest() : this((string?)null) { }
}
