using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Capacity.Availability;
using Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

// Both namespaces define an AvailabilityResult and they are different things: the console's is
// a window of declared slots, the generated one is the agent tool's per-draft answer. Aliasing
// the console's keeps each call site saying which question it asked.
using SlotWindowResult = Cordia.Application.Handlers.Capacity.Availability.AvailabilityResult;

namespace Cordia.Api.Controllers;

/// <summary>
/// What is bookable, how a venue declares it, and what the agent may offer a caller
/// (spec 07 + spec 03 §4.3; docs §12 gap 10).
///
/// GET  /api/availability?from=&amp;to=      → 200 | 400   (console read)
/// POST /api/availability/slots            → 200 | 400 | 403 (owner declares capacity)
/// POST /api/availability/candidates       → 200 | 400   (agent tool)
///
/// <para>The first two ship together deliberately. `capacity_slots` previously had a read and no
/// writer, so it was empty everywhere: no booking was capacity-checked, and an availability read
/// on its own would have reported "nothing available" for every venue — a confident falsehood
/// rather than the honest gap it replaced.</para>
///
/// <para>The third is a different question asked of the same inventory. The console asks "what
/// slots exist in this window"; the agent asks "can THIS draft be served, and hold it if so".
/// They share a route prefix and nothing else — different shapes, different gating, different
/// callers.</para>
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
    [ProducesResponseType(typeof(SlotWindowResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SlotWindowResult>> Get(
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

    /// <summary>
    /// Checks candidate availability for a booking draft and, when <c>want_hold</c> is set,
    /// takes a bounded capacity hold. Repeated calls carrying the same
    /// <c>X-Idempotency-Key</c> return the same hold (Inv-5).
    /// </summary>
    [HttpPost("candidates")]
    [ProducesResponseType(typeof(CheckCandidateAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CheckCandidateAvailabilityResponse>> CheckCandidates(
        [FromBody] AvailabilityRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var holdKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? request.HoldIdempotencyKey
            : idempotencyKey;

        var result = await mediator.Send(
            new CheckCandidateAvailabilityCommand(tenant.TenantId, request, holdKey),
            ct);

        var availability = result.Availability;
        return Ok(new CheckCandidateAvailabilityResponse(
            result.Status,
            availability.Mode,
            availability.Candidates,
            availability.Hold,
            availability.InvalidFields,
            availability.Alternatives));
    }
}

/// <summary>Body for POST /api/availability/slots.</summary>
public sealed record CreateSlotRequest(
    [property: JsonPropertyName("resource_id")] string? ResourceId,
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("location_id")] string? LocationId,
    [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("total_capacity")] int TotalCapacity);

/// <summary>
/// Wire shape for <c>check_candidate_availability</c>: the per-tool status code plus the
/// <c>AvailabilityResult</c> fields flattened alongside it, because the agent's typed tool
/// client reads the payload from the response body root (spec 03 §4.2/§4.3).
/// </summary>
public sealed record CheckCandidateAvailabilityResponse(
    [property: JsonPropertyName("status")] CheckCandidateAvailabilityStatusEnum Status,
    [property: JsonPropertyName("mode")] AvailabilityModeEnum Mode,
    [property: JsonPropertyName("candidates")] IReadOnlyList<AvailabilityCandidate> Candidates,
    [property: JsonPropertyName("hold")] HoldResult? Hold,
    [property: JsonPropertyName("invalid_fields")] IReadOnlyList<string> InvalidFields,
    [property: JsonPropertyName("alternatives")] IReadOnlyList<AvailabilityCandidate> Alternatives);
