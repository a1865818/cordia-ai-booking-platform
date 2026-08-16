using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.DeadLetters;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Dead-letter replay endpoint (spec 05 §6.1, §10, T7).
/// POST /api/dead-letter-messages/replay → 200 | 400 | 404
/// </summary>
[ApiController]
[Route("api/dead-letter-messages")]
public class DeadLetterMessagesController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>
    /// Replays a quarantined dead-letter message according to the §6.1 ID rules.
    /// Returns the replay decision, a new audit replay-command-id, and the dispatched message-id
    /// (null when <c>AckedWithoutReadmit</c>).
    /// </summary>
    [HttpPost("replay")]
    [ProducesResponseType(typeof(ReplayDeadLetterResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReplayDeadLetterResult>> Replay(
        [FromBody] ReplayRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        // With SuppressImplicitRequiredAttributeForNonNullableReferenceTypes on, a missing/null JSON
        // body binds to null rather than auto-400; guard it so we return 400 instead of NRE-ing.
        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON body with messageId, consumerName, and reasonCode is required.");

        TenantGuard.RequireTenant(tenant);

        // Spec 14 §24.5 — operator identity, staff role (GD-1), and reason code are mandatory
        // for replay; a verified session wins over the X-Actor/X-Actor-Role shim (ActorBinder).
        // The validator/behavior reject empty values, the behavior denies role mismatch.
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);
        var result = await mediator.Send(
            new ReplayDeadLetterCommand(
                tenant.TenantId, body.MessageId, body.ConsumerName,
                resolvedActor, body.ReasonCode ?? string.Empty,
                resolvedRole),
            ct);

        return Ok(result);
    }
}

/// <summary>Request body for the replay endpoint. reasonCode is mandatory (spec 14 §24.5).</summary>
public sealed record ReplayRequest(string MessageId, string ConsumerName, string? ReasonCode = null);
