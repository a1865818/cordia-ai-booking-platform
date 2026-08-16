using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Resources.CreateStaffResource;
using Cordia.Application.Handlers.Resources.ListStaffResources;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Named staff members a tenant can assign to work.
///
/// GET  /api/resources → 200 (all, including inactive)
/// POST /api/resources → 201 | 400 | 403 (owner)
///
/// Distinct from <c>/api/tenant-resources</c>, which names bookable services and locations.
/// </summary>
[ApiController]
[Route("api/resources")]
public class ResourcesController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>
    /// Lists staff members, including inactive ones, so settings can show the full roster.
    /// Not owner-gated — the schedule needs the list to label assigned work.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(StaffResourceListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StaffResourceListResult>> List(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new ListStaffResourcesQuery(tenant.TenantId), ct));
    }

    /// <summary>Adds an active staff member. Owner-only and audited.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(StaffResourceView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StaffResourceView>> Create(
        [FromBody] CreateStaffResourceRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var result = await mediator.Send(
            new CreateStaffResourceCommand(
                tenant.TenantId,
                body?.Name ?? string.Empty,
                body?.Role,
                resolvedActor,
                resolvedRole,
                reasonCode ?? string.Empty),
            ct);

        return Created($"/api/resources/{result.ResourceId}", result);
    }
}

public sealed record CreateStaffResourceRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string? Role);
