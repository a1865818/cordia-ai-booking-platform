using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Tenancy.SetTenantDisplayName;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// The tenant's guest-facing venue name (docs §12 gap 11 / §13 B2).
///
/// GET  /api/tenant-profile/display-name → 200 | 400
/// PUT  /api/tenant-profile/display-name → 200 | 400 | 403
///
/// Deliberately NOT a general tenant-profile surface: it holds the one field two ratified
/// contracts already require — spec 08's customer-facing <c>business_name</c> on a
/// <c>ManageBookingCandidate</c>, and the console shell showing a venue name rather than an id.
/// Addresses, branding and trading hours are spec 12's to design.
/// </summary>
[ApiController]
[Route("api/tenant-profile")]
public class TenantProfileController(
    IMediator mediator,
    ITenantContext tenant,
    ITenantDisplayNameRepository displayNames) : ControllerBase
{
    /// <summary>
    /// Reads the tenant's venue name. Not owner-gated: it is what guests are shown, so it is
    /// not a secret — and the console shell needs it on every page.
    ///
    /// A tenant that has not set one gets <c>null</c>, which is a real answer: it is what makes
    /// the phone lookup withhold candidates rather than show a guest a tenant slug.
    /// </summary>
    [HttpGet("display-name")]
    [ProducesResponseType(typeof(TenantDisplayNameResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantDisplayNameResult>> Get(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var name = await displayNames.GetAsync(tenant.TenantId, ct);
        return Ok(new TenantDisplayNameResult(tenant.TenantId, name ?? string.Empty));
    }

    /// <summary>
    /// Sets the venue name. Owner-only and audited — this is the name a guest is shown.
    /// Idempotent: the tenant is the key, so a repeat updates rather than duplicating.
    /// </summary>
    [HttpPut("display-name")]
    [ProducesResponseType(typeof(TenantDisplayNameResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantDisplayNameResult>> Put(
        [FromBody] SetDisplayNameRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new SetTenantDisplayNameCommand(
                tenant.TenantId,
                body?.DisplayName ?? string.Empty,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return Ok(result);
    }
}

/// <summary>Body for PUT /api/tenant-profile/display-name.</summary>
public sealed record SetDisplayNameRequest(
    [property: JsonPropertyName("display_name")] string? DisplayName)
{
    public SetDisplayNameRequest() : this((string?)null) { }
}
