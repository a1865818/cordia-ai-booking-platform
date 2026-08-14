using System.Text.Json.Serialization;
using Cordia.Application.Handlers.AgentKnowledge;
using Cordia.Application.Handlers.AgentKnowledge.CreateAgentKnowledge;
using Cordia.Application.Handlers.AgentKnowledge.UpdateAgentKnowledge;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Agent knowledge-base CRUD (docs/backend-gaps.md G9).
///
/// GET  /api/agent-knowledge      → 200 | 400
/// POST /api/agent-knowledge      → 201 | 400 | 403
/// PUT  /api/agent-knowledge/{id} → 200 | 400 | 403 | 404
/// </summary>
[ApiController]
[Route("api/agent-knowledge")]
public class AgentKnowledgeController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Lists knowledge entries. Not owner-gated.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AgentKnowledgeListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AgentKnowledgeListResult>> List(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetAgentKnowledgeQuery(tenant.TenantId), ct));
    }

    /// <summary>
    /// Creates a manual entry. Owner-only. Empty <c>a</c> → <c>needs-answer</c>; otherwise
    /// <c>answered</c>. Source is <c>manual</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AgentKnowledgeEntryResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentKnowledgeEntryResult>> Create(
        [FromBody] CreateAgentKnowledgeRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new CreateAgentKnowledgeCommand(
                tenant.TenantId,
                body?.Q ?? string.Empty,
                body?.A,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return Created($"/api/agent-knowledge/{result.Id}", result);
    }

    /// <summary>Updates answer and/or status. Owner-only. Unknown id → 404.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(AgentKnowledgeEntryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentKnowledgeEntryResult>> Update(
        string id,
        [FromBody] UpdateAgentKnowledgeRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new UpdateAgentKnowledgeCommand(
                tenant.TenantId,
                id,
                body?.A,
                body?.Status,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return result is null ? NotFound() : Ok(result);
    }
}

/// <summary>Body for POST /api/agent-knowledge.</summary>
public sealed record CreateAgentKnowledgeRequest(
    [property: JsonPropertyName("q")] string? Q,
    [property: JsonPropertyName("a")] string? A)
{
    public CreateAgentKnowledgeRequest() : this(null, null) { }
}

/// <summary>Body for PUT /api/agent-knowledge/{id}.</summary>
public sealed record UpdateAgentKnowledgeRequest(
    [property: JsonPropertyName("a")] string? A,
    [property: JsonPropertyName("status")] string? Status)
{
    public UpdateAgentKnowledgeRequest() : this(null, null) { }
}
