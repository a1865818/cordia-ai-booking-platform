using System.Text.Json.Serialization;
using Cordia.Application.Handlers.BookingDrafts.ValidateBookingDraft;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// The policy gate the agent asks BEFORE it asks about capacity (spec 03 §4.3, §6.1).
///
/// POST /api/booking-drafts/validate → 200 | 400
///
/// <para>Separate from <c>POST /api/availability/candidates</c> on purpose: that one answers
/// "is there a table free", which is a question about inventory, and it answered it happily
/// for a party of 400 at 3am because neither the party size nor the clock is anything a
/// capacity row knows about.</para>
/// </summary>
[ApiController]
[Route("api/booking-drafts")]
public class BookingDraftsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Checks a draft against tenant policy and returns a customer-facing message when it
    /// fails. A failing draft is a 200: the caller is on the phone and can still fix it.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateBookingDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ValidateBookingDraftResponse>> Validate(
        [FromBody] BookingDraft draft,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new ValidateBookingDraftQuery(tenant.TenantId, draft), ct);
        var validation = result.Validation;

        return Ok(new ValidateBookingDraftResponse(
            result.Status,
            validation.Valid,
            validation.Code,
            validation.CustomerMessage,
            validation.RecoverableFields,
            validation.SuggestedAction,
            validation.PolicyVersion));
    }
}

/// <summary>
/// Wire shape for <c>validate_booking_draft</c>: the per-tool status code plus the
/// <c>PolicyValidationResult</c> fields flattened alongside it, because the agent's typed
/// tool client reads the payload from the response body root (spec 03 §4.2/§4.3).
/// </summary>
public sealed record ValidateBookingDraftResponse(
    [property: JsonPropertyName("status")] ValidateBookingDraftStatusEnum Status,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("code")] ValidateBookingDraftStatusEnum Code,
    [property: JsonPropertyName("customer_message")] string CustomerMessage,
    [property: JsonPropertyName("recoverable_fields")] IReadOnlyList<string> RecoverableFields,
    [property: JsonPropertyName("suggested_action")] string? SuggestedAction,
    [property: JsonPropertyName("policy_version")] string PolicyVersion);
