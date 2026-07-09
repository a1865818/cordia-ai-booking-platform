using Cordia.Application.Handlers.Security.RecordConsent;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Conversation-session consent endpoint (spec 14 §5.1, D12: the gateway
/// creates the row; this endpoint only updates its consent_* columns). Wire
/// shapes are the generated cross-service contracts (SessionConsentState /
/// ConsentRecordedResult) so agent↔API drift is impossible.
/// </summary>
[ApiController]
[Route("api/conversation-sessions")]
public class ConversationSessionsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    [HttpPost("{sessionId}/consent")]
    [ProducesResponseType(typeof(ConsentRecordedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConsentRecordedResult>> RecordConsent(
        string sessionId,
        [FromBody] SessionConsentState? consent,
        [FromHeader(Name = "X-Correlation-Id")] string? correlationId,
        CancellationToken ct)
    {
        // With SuppressImplicitRequiredAttributeForNonNullableReferenceTypes on,
        // a missing/null JSON body binds to null rather than auto-400; guard it
        // so we return 400 instead of NRE-ing (same trap as the replay endpoint).
        if (consent is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON SessionConsentState body is required.");

        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var result = await mediator.Send(
            RecordConsentCommand.FromContract(
                tenant.TenantId,
                sessionId,
                consent,
                string.IsNullOrWhiteSpace(correlationId) ? HttpContext.TraceIdentifier : correlationId),
            ct);

        return Ok(new ConsentRecordedResult
        {
            SessionId = result.SessionId,
            RecordingAllowed = result.RecordingAllowed,
        });
    }
}
