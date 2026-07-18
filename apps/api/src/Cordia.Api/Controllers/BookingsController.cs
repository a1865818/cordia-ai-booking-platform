using System.Text.Json.Serialization;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.ManageBooking.FindManageableBookings;
using Cordia.Application.Handlers.ManageBooking.GetBookingDetails;
using Cordia.Application.Handlers.ManageBooking.SubmitBookingCancellation;
using Cordia.Application.Handlers.ManageBooking.SubmitBookingModification;
using Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Acceptance endpoint for confirmed booking intent submissions (spec 04 §3, §6.1).
/// POST /api/bookings → 202 Accepted | 409 Conflict | 422 Unprocessable
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BookingsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Finds minimized, non-sensitive booking candidates for Manage Booking.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FindManageableBookingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FindManageableBookingsResponse>> FindManageable(
        [FromQuery(Name = "phone_e164")] string? phoneE164,
        [FromQuery(Name = "booking_reference")] string? bookingReference,
        [FromQuery(Name = "email")] string? email,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new FindManageableBookingsQuery(tenant.TenantId, phoneE164, bookingReference, email),
            ct);

        return Ok(new FindManageableBookingsResponse(result.Status, result.Candidates));
    }

    /// <summary>Returns a verification-leveled booking projection.</summary>
    [HttpGet("{bookingId}")]
    [ProducesResponseType(typeof(GetBookingDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(GetBookingDetailsResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(GetBookingDetailsResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetBookingDetailsResponse>> GetDetails(
        string bookingId,
        [FromHeader(Name = "X-Grant-Id")] string? grantHeader,
        [FromQuery(Name = "grant_id")] string? grantQuery,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var grantId = string.IsNullOrWhiteSpace(grantHeader) ? grantQuery : grantHeader;
        var result = await mediator.Send(new GetBookingDetailsQuery(tenant.TenantId, bookingId, grantId), ct);
        var response = new GetBookingDetailsResponse(result.Status, result.Booking);

        return result.Status switch
        {
            GetBookingDetailsStatusEnum.NotFound => NotFound(response),
            GetBookingDetailsStatusEnum.RequiresVerification => StatusCode(StatusCodes.Status403Forbidden, response),
            _ => Ok(response),
        };
    }

    /// <summary>Validates a proposed booking change without accepting a mutation.</summary>
    [HttpPost("{bookingId}/changes:validate")]
    [ProducesResponseType(typeof(ValidateBookingChangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ValidateBookingChangeResponse>> ValidateChange(
        string bookingId,
        [FromBody] ValidateBookingChangeRequest request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var targetBookingId = string.IsNullOrWhiteSpace(request.TargetBookingId)
            ? bookingId
            : request.TargetBookingId;

        var result = await mediator.Send(
            new ValidateBookingChangeQuery(
                tenant.TenantId,
                bookingId,
                targetBookingId,
                request.ExpectedBookingVersion,
                request.RequestedFields,
                request.GrantId),
            ct);

        return Ok(new ValidateBookingChangeResponse(result.Status, result.Validation));
    }

    /// <summary>Durably accepts a confirmed manage-booking modification.</summary>
    [HttpPost("{bookingId}/modifications")]
    [ProducesResponseType(typeof(SubmitBookingModificationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(SubmitBookingModificationResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(SubmitBookingModificationResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SubmitBookingModificationResponse>> SubmitModification(
        string bookingId,
        [FromBody] SubmitModificationInput request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new SubmitBookingModificationCommand(tenant.TenantId, bookingId, request), ct);

        return result.Outcome switch
        {
            SubmitBookingModificationOutcome.Accepted => Accepted(result.Body.StatusUrl, result.Body),
            SubmitBookingModificationOutcome.StaleVersion => Conflict(result.Body),
            SubmitBookingModificationOutcome.Conflict => Conflict(result.Body),
            SubmitBookingModificationOutcome.ConfirmationInvalid => UnprocessableEntity(result.Body),
            _ => throw new InvalidOperationException($"Unexpected modification outcome: {result.Outcome}"),
        };
    }

    /// <summary>Durably accepts a confirmed manage-booking cancellation.</summary>
    [HttpPost("{bookingId}/cancellations")]
    [ProducesResponseType(typeof(SubmitBookingCancellationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(SubmitBookingCancellationResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubmitBookingCancellationResponse>> SubmitCancellation(
        string bookingId,
        [FromBody] SubmitCancellationInput request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new SubmitBookingCancellationCommand(tenant.TenantId, bookingId, request), ct);

        return result.Outcome switch
        {
            SubmitBookingCancellationOutcome.Accepted => Accepted(result.Body.StatusUrl, result.Body),
            SubmitBookingCancellationOutcome.StaleVersion => Conflict(result.Body),
            SubmitBookingCancellationOutcome.Conflict => Conflict(result.Body),
            SubmitBookingCancellationOutcome.AlreadyCancelled => Conflict(result.Body),
            _ => throw new InvalidOperationException($"Unexpected cancellation outcome: {result.Outcome}"),
        };
    }

    /// <summary>
    /// Durably accepts a confirmed booking intent.
    /// Returns 202 with a <c>Location</c> header pointing to the status URL (spec 04 §10.4).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OperationAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(IdempotencyConflict), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitConfirmedIntentRequest request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new SubmitConfirmedIntentCommand(tenant.TenantId, request), ct);

        return result switch
        {
            Accepted a => Accepted(a.Body.StatusUrl, a.Body),
            Conflict c => Conflict(c.Detail),
            _ => throw new InvalidOperationException($"Unexpected AcceptanceResult type: {result.GetType().Name}"),
        };
    }
}

public sealed record FindManageableBookingsResponse(
    [property: JsonPropertyName("status")] FindStatus Status,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ManageableBooking> Candidates);

public sealed record GetBookingDetailsResponse(
    [property: JsonPropertyName("status")] GetBookingDetailsStatusEnum Status,
    [property: JsonPropertyName("booking")] BookingProjectionRef? Booking);

public sealed record ValidateBookingChangeRequest(
    [property: JsonPropertyName("target_booking_id")] string TargetBookingId,
    [property: JsonPropertyName("expected_booking_version")] long ExpectedBookingVersion,
    [property: JsonPropertyName("requested_fields")] System.Text.Json.JsonElement RequestedFields,
    [property: JsonPropertyName("grant_id")] string GrantId);

public sealed record ValidateBookingChangeResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("validation")] BookingChangeValidation Validation);
