using Cordia.Application.Handlers.Tenancy.GetTenantContext;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// The venue as the AGENT needs it (spec 03 §4.3).
///
/// GET /api/tenant-context → 200 | 400
///
/// Distinct from <c>/api/tenant-profile</c>, which serves the operator console: that one is
/// shaped for a settings screen (display name, business type, the waitlist switch) and is
/// writable. This is read-only, shaped by the agent's <c>TenantContextView</c> contract, and
/// carries the two things a console never needed and a phone call cannot work without —
/// the venue's timezone and its currency.
/// </summary>
[ApiController]
[Route("api/tenant-context")]
public class TenantContextController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Reads tenant policy, timezone, currency and capabilities for the agent.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(TenantContextResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantContextResult>> Get(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // The SIGNED TenantContext is authoritative for policy version (Inv-13) and the agent
        // already holds it: resolve_tenant stamps tenant_policy_version from the token before
        // load_context ever calls this, and load_context only backfills when that is unset.
        // ITenantContext here exposes TenantId alone, and deriving a version from the endpoint
        // row would be a second, less authoritative source for a value the caller already has.
        // So this reports that it is not the authority rather than guessing.
        return Ok(await mediator.Send(
            new GetTenantContextQuery(tenant.TenantId, GetTenantContextQueryHandler.UnknownPolicyVersion), ct));
    }
}
