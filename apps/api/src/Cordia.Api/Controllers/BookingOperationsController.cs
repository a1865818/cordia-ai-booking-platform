using Cordia.Application.Handlers.BookingOperations.GetOperationStatus;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Status-polling endpoint for booking operations (spec 04 §3, spec 03 <c>get_operation_status</c>).
/// GET /api/booking-operations/{operationId} → 200 | 404
/// </summary>
[ApiController]
[Route("api/booking-operations")]
public class BookingOperationsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Returns the current status of the given booking operation, or 404 if not found.</summary>
    [HttpGet("{operationId}")]
    [ProducesResponseType(typeof(OperationStatusView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationStatusView>> GetStatus(
        string operationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var status = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        return status is null ? NotFound() : Ok(status);
    }
}
