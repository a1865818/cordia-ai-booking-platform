using Cordia.Application.Handlers.TenantResources.ResolveTenantResources;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// What this tenant offers, by name (agent tool <c>resolve_tenant_resources</c>).
///
/// GET /api/tenant-resources?kind=&amp;query= → 200 | 400
///
/// The slot extractor may never invent an identifier from something a customer
/// said, which left "book dinner at the Fitzroy room" unusable: perfectly
/// recognised, and nothing could turn it into a payload. This route is the
/// closed set that makes the conversion safe — a name resolves only to an
/// identifier the tenant has actually declared, and anything else comes back
/// <c>no_match</c> so the agent asks instead of guessing.
/// </summary>
[ApiController]
[Route("api/tenant-resources")]
public class TenantResourcesController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Resolves a spoken name, or lists what exists when no query is given.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ResolvedResourcesResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResolvedResourcesResult>> Resolve(
        [FromQuery(Name = "kind")] string? kind,
        [FromQuery(Name = "query")] string? query,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        return Ok(await mediator.Send(
            new ResolveTenantResourcesQuery(tenant.TenantId, kind, query), ct));
    }
}
