using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Agent-facing candidate-availability endpoint (spec 03 §4.3).
/// POST /api/availability/candidates → 200 with the flattened AvailabilityResult.
/// </summary>
[ApiController]
[Route("api/availability")]
public class AvailabilityController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Checks candidate availability for a booking draft and, when <c>want_hold</c> is set,
    /// takes a bounded capacity hold. Repeated calls carrying the same
    /// <c>X-Idempotency-Key</c> return the same hold (Inv-5).
    /// </summary>
    [HttpPost("candidates")]
    [ProducesResponseType(typeof(CheckCandidateAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
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
