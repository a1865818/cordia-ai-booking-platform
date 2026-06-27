using Cordia.Application.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
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
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var result = await mediator.Send(new SubmitConfirmedIntentCommand(tenant.TenantId, request), ct);

        return result switch
        {
            Accepted a => Accepted(a.Body.StatusUrl, a.Body),
            Conflict c => Conflict(c.Detail),
            _ => throw new InvalidOperationException($"Unexpected AcceptanceResult type: {result.GetType().Name}"),
        };
    }
}
