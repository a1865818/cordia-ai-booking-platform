using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.AgentPolicy;
using Cordia.Application.Handlers.AgentPolicy.SetAgentGuardrails;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Agent policy toggles (docs/backend-gaps.md G8).
///
/// GET  /api/agent-policy/guardrails → 200 | 400
/// PUT  /api/agent-policy/guardrails → 200 | 400 | 403
/// </summary>
[ApiController]
[Route("api/agent-policy")]
public class AgentPolicyController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>
    /// Reads canonical guardrails with labels. Not owner-gated. Missing row → g1–g5 on, g6 off;
    /// locked rails (g1, g4) are always on.
    /// </summary>
    [HttpGet("guardrails")]
    [ProducesResponseType(typeof(AgentGuardrailsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AgentGuardrailsResult>> GetGuardrails(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetAgentGuardrailsQuery(tenant.TenantId), ct));
    }

    /// <summary>
    /// Upserts guardrail toggles. Owner-only and audited. Locked rails ignore a client false.
    /// A present g5 also upserts <c>tenant_profiles.waitlist_auto_book</c>.
    /// </summary>
    [HttpPut("guardrails")]
    [ProducesResponseType(typeof(AgentGuardrailsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentGuardrailsResult>> PutGuardrails(
        [FromBody] SetAgentGuardrailsRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var toggles = (body?.Guardrails ?? [])
            .Where(g => g is not null)
            .Select(g => new GuardrailToggle(g.Id ?? string.Empty, g.On))
            .ToList();

        var result = await mediator.Send(
            new SetAgentGuardrailsCommand(
                tenant.TenantId,
                toggles,
                resolvedActor,
                reasonCode ?? string.Empty,
                resolvedRole),
            ct);

        return Ok(result);
    }
}

/// <summary>Body for PUT /api/agent-policy/guardrails.</summary>
public sealed record SetAgentGuardrailsRequest(
    [property: JsonPropertyName("guardrails")] IReadOnlyList<GuardrailWriteItem>? Guardrails)
{
    public SetAgentGuardrailsRequest() : this((IReadOnlyList<GuardrailWriteItem>?)null) { }
}

public sealed record GuardrailWriteItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("locked")] bool Locked)
{
    public GuardrailWriteItem() : this(null, null, false, false) { }
}
