using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Security.GetModelTrace;
using Cordia.Application.Handlers.Security.RecordModelTrace;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Model-trace telemetry endpoints (spec 13 §25.3 stricter access/retention tier, enforced by
/// the spec-14 storage + authorization boundary).
///
/// POST /api/model-traces              → 200 (agent-plane write; redacted content only)
/// GET  /api/model-traces/{traceId}    → 200 | 403 | 404 (staff role + reason code; every
///                                        view audited as model_trace.viewed — spec 13 §4.3)
/// Traces are stored encrypted with the GD-3 14-day TTL (strictly shorter than transcripts)
/// and are NEVER written to app logs.
/// </summary>
[ApiController]
[Route("api/model-traces")]
public class ModelTracesController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Stores one redacted model prompt/output under the telemetry retention tier.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ModelTraceRecordedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ModelTraceRecordedResult>> Record(
        [FromBody] ModelTraceRequest? body,
        CancellationToken ct)
    {
        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON model-trace body is required.");

        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new RecordModelTraceCommand(
                tenant.TenantId, body.TraceId ?? string.Empty, body.SessionId,
                body.Kind ?? string.Empty, body.Content ?? string.Empty),
            ct);

        return Ok(result);
    }

    /// <summary>Staff-only trace view; allow AND deny land on the audit trail (§4.3).</summary>
    [HttpGet("{traceId}")]
    [ProducesResponseType(typeof(ModelTraceView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelTraceView>> Get(
        string traceId,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var view = await mediator.Send(
            new GetModelTraceQuery(tenant.TenantId, traceId, actor, actorRole, reasonCode), ct);

        return view is null ? NotFound() : Ok(view);
    }
}

/// <summary>Body for POST /api/model-traces.</summary>
public sealed record ModelTraceRequest(
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("content")] string? Content)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public ModelTraceRequest() : this(null, null, null, null) { }
}
