using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Tenancy.GetOperatingHours;
using Cordia.Application.Handlers.Tenancy.GetTenantProfile;
using Cordia.Application.Handlers.Tenancy.SetOperatingHours;
using Cordia.Application.Handlers.Tenancy.SetTenantDisplayName;
using Cordia.Application.Handlers.Tenancy.SetTenantProfile;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Tenant business profile, guest-facing venue name, and operating hours.
///
/// GET  /api/tenant-profile              → 200 | 400
/// PUT  /api/tenant-profile              → 200 | 400 | 403
/// GET  /api/tenant-profile/display-name → 200 | 400
/// PUT  /api/tenant-profile/display-name → 200 | 400 | 403
/// GET  /api/tenant-profile/hours        → 200 | 400
/// PUT  /api/tenant-profile/hours        → 200 | 400 | 403
/// </summary>
[ApiController]
[Route("api/tenant-profile")]
public class TenantProfileController(
    IMediator mediator,
    ITenantContext tenant,
    ITenantDisplayNameRepository displayNames) : ControllerBase
{
    /// <summary>
    /// Reads the tenant business profile joined with the guest-facing venue name.
    /// Not owner-gated. A missing profile row yields <c>business_type: null</c> and
    /// <c>waitlist_auto_book: false</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(TenantProfileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantProfileResult>> GetProfile(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetTenantProfileQuery(tenant.TenantId), ct));
    }

    /// <summary>
    /// Partial update of business type and/or the venue waitlist auto-book switch. Owner-only
    /// and audited. Does not write agent guardrails.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(TenantProfileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantProfileResult>> PutProfile(
        [FromBody] SetTenantProfileRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new SetTenantProfileCommand(
                tenant.TenantId,
                body?.BusinessType,
                body?.WaitlistAutoBook,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return Ok(result);
    }

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

    /// <summary>
    /// Reads the seven-day operating hours. Not owner-gated. Empty stored hours return the
    /// seed default: Monday–Saturday 09:00–18:00 on, Sunday off.
    /// </summary>
    [HttpGet("hours")]
    [ProducesResponseType(typeof(OperatingHoursResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatingHoursResult>> GetHours(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetOperatingHoursQuery(tenant.TenantId), ct));
    }

    /// <summary>Replaces the seven-day operating hours. Owner-only and audited.</summary>
    [HttpPut("hours")]
    [ProducesResponseType(typeof(OperatingHoursResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OperatingHoursResult>> PutHours(
        [FromBody] SetOperatingHoursRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new SetOperatingHoursCommand(
                tenant.TenantId,
                body?.Hours ?? [],
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

/// <summary>Body for PUT /api/tenant-profile.</summary>
public sealed record SetTenantProfileRequest(
    [property: JsonPropertyName("business_type")] string? BusinessType,
    [property: JsonPropertyName("waitlist_auto_book")] bool? WaitlistAutoBook)
{
    public SetTenantProfileRequest() : this(null, null) { }
}

/// <summary>Body for PUT /api/tenant-profile/hours.</summary>
public sealed record SetOperatingHoursRequest(
    [property: JsonPropertyName("hours")] IReadOnlyList<OperatingHoursDay>? Hours)
{
    public SetOperatingHoursRequest() : this((IReadOnlyList<OperatingHoursDay>?)null) { }
}
