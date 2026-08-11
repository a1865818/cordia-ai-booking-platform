using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Capacity.Availability;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// What is bookable, and how a venue declares it (spec 07; docs §12 gap 10).
///
/// GET  /api/availability?from=&amp;to=  → 200 | 400
/// POST /api/availability/slots        → 200 | 400 | 403
///
/// <para>The two ship together deliberately. `capacity_slots` previously had a read and no
/// writer, so it was empty everywhere: no booking was capacity-checked, and an availability read
/// on its own would have reported "nothing available" for every venue — a confident falsehood
/// rather than the honest gap it replaced.</para>
/// </summary>
[ApiController]
[Route("api/availability")]
public class AvailabilityController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Slots starting in the window, with the free capacity each has.
    ///
    /// Not owner-gated: an operator taking a booking needs it. An empty list is a real answer —
    /// it means no slot has been declared for that window, which is different from the venue
    /// being full, and the console must not conflate them.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AvailabilityResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AvailabilityResult>> Get(
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var start = from ?? DateTimeOffset.UtcNow;
        var end = to ?? start.AddDays(7);

        return Ok(await mediator.Send(new AvailabilityQuery(tenant.TenantId, start, end), ct));
    }

    /// <summary>
    /// Declares a bookable slot. Owner-only and audited — capacity is a commercial statement
    /// about what the venue will honour.
    ///
    /// Idempotent by the derived slot key, so re-declaring the same slot returns the existing one
    /// rather than duplicating it or resetting counts that live bookings depend on.
    /// </summary>
    [HttpPost("slots")]
    [ProducesResponseType(typeof(AvailableSlot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AvailableSlot>> CreateSlot(
        [FromBody] CreateSlotRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON body is required.");

        var result = await mediator.Send(
            new CreateCapacitySlotCommand(
                tenant.TenantId,
                body.ResourceId,
                body.ServiceId,
                body.LocationId,
                body.StartsAt,
                body.TotalCapacity,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return Ok(result);
    }
}

/// <summary>Body for POST /api/availability/slots.</summary>
public sealed record CreateSlotRequest(
    [property: JsonPropertyName("resource_id")] string? ResourceId,
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("location_id")] string? LocationId,
    [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("total_capacity")] int TotalCapacity);
