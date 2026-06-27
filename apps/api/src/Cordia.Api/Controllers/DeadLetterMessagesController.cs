using Cordia.Application.Messaging.Replay;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Dead-letter replay endpoint (spec 05 §6.1, §10, T7).
/// POST /api/dead-letter-messages/replay → 200 | 400 | 404
/// </summary>
[ApiController]
[Route("api/dead-letter-messages")]
public class DeadLetterMessagesController(IMediator mediator, ITenantContext tenant) : ControllerBase
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
        [FromBody] ReplayRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var result = await mediator.Send(
            new ReplayDeadLetterCommand(tenant.TenantId, body.MessageId, body.ConsumerName),
            ct);

        return Ok(result);
    }
}

/// <summary>Request body for the replay endpoint.</summary>
public sealed record ReplayRequest(string MessageId, string ConsumerName);
