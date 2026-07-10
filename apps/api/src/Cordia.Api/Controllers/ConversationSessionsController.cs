using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Security.AppendTranscriptTurn;
using Cordia.Application.Handlers.Security.GetTranscript;
using Cordia.Application.Handlers.Security.RecordConsent;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Conversation-session consent + transcript endpoints (spec 14 §5.1/§4.3, D12: the gateway
/// creates the row; consent updates its consent_* columns; transcript turns live in the
/// encrypted object store keyed off this row — no session write). Wire
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

        TenantGuard.RequireTenant(tenant);

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

    /// <summary>
    /// Persists one REDACTED transcript turn into the encrypted object store (spec 14 §4.3,
    /// §5.1/§5.2). Fail-closed on consent: when the session's recording_allowed is not true the
    /// turn is suppressed and <c>persisted=false</c> is returned — a normal outcome, not an
    /// error. The caller (the spec 01/02 pipeline via the agent tool client) must redact BEFORE
    /// posting; this lane re-sanitizes regardless (Inv-12). Inv-1: persist-only, no side effects.
    /// </summary>
    [HttpPost("{sessionId}/transcript-turns")]
    [ProducesResponseType(typeof(TranscriptTurnPersistedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TranscriptTurnPersistedResult>> AppendTranscriptTurn(
        string sessionId,
        [FromBody] TranscriptTurnRequest? body,
        CancellationToken ct)
    {
        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON transcript-turn body is required.");

        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new AppendTranscriptTurnCommand(
                tenant.TenantId, sessionId, body.Seq,
                body.TurnId ?? string.Empty, body.Speaker ?? string.Empty, body.Text ?? string.Empty),
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Consent-gated sensitive read of the session's persisted transcript (spec 14 §3.1/§5.1).
    /// Authorization (Inv-11 — session identity alone never reads): EITHER a spec-08
    /// read_sensitive verification grant (X-Grant-Id header or grant_id query, scoped to the
    /// session's customer) OR a staff role (X-Actor + X-Actor-Role satisfying operator +
    /// X-Reason-Code). Consent absent/declined → 409 Consent required. Allow AND deny audited.
    /// </summary>
    [HttpGet("{sessionId}/transcript")]
    [ProducesResponseType(typeof(TranscriptView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TranscriptView>> GetTranscript(
        string sessionId,
        [FromHeader(Name = "X-Grant-Id")] string? grantHeader,
        [FromQuery(Name = "grant_id")] string? grantQuery,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // Header wins over query (same precedence as BookingsController.GetDetails).
        var grantId = string.IsNullOrWhiteSpace(grantHeader) ? grantQuery : grantHeader;

        var view = await mediator.Send(
            new GetTranscriptQuery(tenant.TenantId, sessionId, grantId, actor, actorRole, reasonCode), ct);

        return Ok(view);
    }
}

/// <summary>Body for POST /api/conversation-sessions/{sessionId}/transcript-turns.</summary>
public sealed record TranscriptTurnRequest(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("turn_id")] string? TurnId,
    [property: JsonPropertyName("speaker")] string? Speaker,
    [property: JsonPropertyName("text")] string? Text)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public TranscriptTurnRequest() : this(0, null, null, null) { }
}
