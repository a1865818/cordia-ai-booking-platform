using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Preorder.ManualResolution;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Operator-facing resolution endpoints for a pre-order saga parked in <c>ManualReview</c> (spec 11
/// §21.4). A pre-order must not linger after a failed/cancelled booking unless an operator AND the
/// customer explicitly convert it to a standalone order — or the operator cancels it.
///
/// POST /api/preorder/sagas/{sagaId}/convert-standalone → 200 | 400 | 404 | 409
/// POST /api/preorder/sagas/{sagaId}/cancel             → 200 | 400 | 404 | 409
///
/// Thin by design: resolves tenant (X-Tenant-Id) + operator (X-Actor), sends a MediatR command, and
/// maps typed domain exceptions via the GlobalExceptionHandler (saga not found → 404, illegal
/// transition → 409, missing operator/customer evidence → 400). Full operator authorization is the
/// spec-08 seam (TODO); every action is audited with the operator + customer-agreement evidence.
/// </summary>
[ApiController]
[Route("api/preorder")]
public class PreorderController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Converts a parked pre-order to a standalone order (operator + recorded customer agreement).</summary>
    [HttpPost("sagas/{sagaId}/convert-standalone")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConvertStandalone(
        string sagaId,
        [FromBody] ConvertStandaloneRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new ConvertPreorderToStandaloneCommand(
            tenant.TenantId, sagaId, ResolveOperator(actor), body.CustomerAgreementEvidenceId ?? string.Empty), ct);

        return Ok(new { saga_id = sagaId, status = "converted_to_standalone" });
    }

    /// <summary>Cancels a parked pre-order (operator resolution — the §21.4 "or cancel" path).</summary>
    [HttpPost("sagas/{sagaId}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string sagaId,
        [FromBody] CancelPreorderRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new CancelPreorderManualReviewCommand(
            tenant.TenantId, sagaId, ResolveOperator(actor), body?.Reason), ct);

        return Ok(new { saga_id = sagaId, status = "cancelled" });
    }

    private static string ResolveOperator(string? actor)
        => string.IsNullOrWhiteSpace(actor) ? "operator" : actor;
}

/// <summary>Body for POST /api/preorder/sagas/{sagaId}/convert-standalone.</summary>
public sealed record ConvertStandaloneRequest(
    [property: JsonPropertyName("customer_agreement_evidence_id")] string? CustomerAgreementEvidenceId)
{
    public ConvertStandaloneRequest() : this((string?)null) { }
}

/// <summary>Body for POST /api/preorder/sagas/{sagaId}/cancel.</summary>
public sealed record CancelPreorderRequest(
    [property: JsonPropertyName("reason")] string? Reason)
{
    public CancelPreorderRequest() : this((string?)null) { }
}
