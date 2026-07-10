using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Application.Handlers.BookingFulfillment.Cancellation;
using Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;
using Cordia.Application.Handlers.BookingOperations.GetOperationStatus;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// REST endpoints for booking operations (spec 04 §3, spec 06 §15.5, §17.4, T11a).
///
/// GET  /api/booking-operations/{operationId}                    → 200 | 404
/// POST /api/booking-operations/{operationId}/cancel             → 202 | 404
/// POST /api/booking-operations/{operationId}/resume             → 202 | 404 | 409
/// POST /api/booking-operations/{operationId}/resolve            → 202 | 400 | 404 | 409
/// POST /api/booking-operations/{operationId}/alternatives       → 202 | 400 | 404 | 409
///
/// Note: POST /cancel sets a cancellation-pending flag that in-flight workflow steps observe at
/// their next checkpoint (FM#13, spec 06 §15.5).  Post-booking cancellation (an already-Booked
/// operation) is a separate new operation defined in spec 08 and is NOT handled here.
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
        TenantGuard.RequireTenant(tenant);

        var status = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>
    /// Requests cancellation of an in-flight booking operation (spec 06 §15.5, FM#13).
    ///
    /// Sets a cancellation-pending flag that in-flight workflow steps observe at their next
    /// checkpoint. Returns 202 if the flag was newly set or was already pending; 404 if the
    /// operation is not found.
    ///
    /// This endpoint does NOT cancel an already-Booked operation — that is a separate new-
    /// operation flow defined in spec 08.
    ///
    /// T11 TODO: surface cancellation_pending=true in the status projection.
    /// </summary>
    [HttpPost("{operationId}/cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestCancellation(
        string operationId,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // Existence check (also covers tenant mismatch — GetOperationStatusQuery returns null).
        var op = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        if (op is null)
            return NotFound();

        // X2: terminal operations cannot be cancelled via this endpoint.
        // Post-booking cancellation (already-Booked) is a separate flow defined in spec 08.
        if (op.Terminal)
            return Conflict(new
            {
                operation_id = operationId,
                detail = "Terminal operations cannot be cancelled via this endpoint. " +
                         "Post-booking cancellation is a separate flow (spec 08).",
            });

        // Idempotent: 202 whether the flag was newly set or was already pending (§15.5).
        await mediator.Send(new RequestCancellationCommand(tenant.TenantId, operationId), ct);
        return Accepted(new { operation_id = operationId, cancellation_pending = true });
    }

    /// <summary>
    /// Authorized controlled retry for a ManualReview operation (spec 06 Inv-15, §17.4).
    ///
    /// Transitions the operation ManualReview → Reconciling and emits a
    /// <c>booking.command.requested.v1</c> continuation so the driver re-drives from the current
    /// durable state. The reconcile step reuses the SAME ProviderIdempotencyKey stored on the
    /// workflow_step row — attempt count is never reset (Inv-15).
    ///
    /// A server-side ReplayCommandId (<c>rpl_{guid}</c>) is generated if not provided.
    /// Spec 14 §24.5 fail-closed (gate decision 2026-07-10): <c>X-Actor</c> and
    /// <c>X-Reason-Code</c> are MANDATORY — missing values are rejected by the command
    /// validator / operator behavior (400), never defaulted.
    ///
    /// 202 Accepted on success; 404 if the operation is not found; 409 if the operation is not
    /// in ManualReview (OperationConflictException → GlobalExceptionHandler).
    /// </summary>
    [HttpPost("{operationId}/resume")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResumeWorkflowStep(
        string operationId,
        [FromBody] ResumeRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // Existence check: 404 if not found (also covers tenant mismatch).
        var existing = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        if (existing is null)
            return NotFound();

        var replayCommandId = body?.ReplayCommandId is { Length: > 0 } supplied
            ? supplied
            : $"rpl_{Guid.NewGuid():N}";
        // Spec 14 §24.5 fail-closed: no "ops" default — empty actor is rejected downstream.
        var resolvedActor = string.IsNullOrWhiteSpace(actor) ? string.Empty : actor;

        // OperationConflictException (not ManualReview, or concurrent change) → 409 via GlobalExceptionHandler.
        await mediator.Send(
            new ResumeWorkflowStepCommand(
                tenant.TenantId, operationId, replayCommandId, resolvedActor,
                reasonCode ?? string.Empty, actorRole ?? string.Empty), ct);

        return Accepted(new { operation_id = operationId, replay_command_id = replayCommandId });
    }

    /// <summary>
    /// Human resolution of a ManualReview operation (spec 06 §11, §17.4, Inv-15).
    ///
    /// <c>resolution</c> must be <c>"booked"</c> (requires <c>provider_ref</c>) or
    /// <c>"failed_final"</c>. Both are §11 legal edges from ManualReview.
    ///
    /// 202 Accepted on success; 400 if the body is invalid; 404 if the operation is not found;
    /// 409 if the operation is not in ManualReview (OperationConflictException → GlobalExceptionHandler).
    /// </summary>
    [HttpPost("{operationId}/resolve")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResolveManualReview(
        string operationId,
        [FromBody] ResolveRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // Map resolution string (case-insensitive) → enum.
        ManualReviewResolution resolution;
        if (string.Equals(body.Resolution, "booked", StringComparison.OrdinalIgnoreCase))
            resolution = ManualReviewResolution.Booked;
        else if (string.Equals(body.Resolution, "failed_final", StringComparison.OrdinalIgnoreCase))
            resolution = ManualReviewResolution.FailedFinal;
        else
            return Problem(statusCode: 400, title: "Invalid resolution",
                detail: $"Resolution must be 'booked' or 'failed_final', got '{body.Resolution}'.");

        // C7: validate provider_ref here so the controller returns 400, not 409.
        // The handler also guards this, but a missing provider_ref is a caller error (400),
        // not a state-conflict error (409).
        if (resolution == ManualReviewResolution.Booked
            && string.IsNullOrWhiteSpace(body.ProviderRef))
            return Problem(statusCode: 400, title: "Missing provider_ref",
                detail: "provider_ref is required when resolution is 'booked'.");

        // Existence check: 404 if not found.
        var existing = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        if (existing is null)
            return NotFound();

        var replayCommandId = body.ReplayCommandId is { Length: > 0 } supplied
            ? supplied
            : $"rpl_{Guid.NewGuid():N}";
        // Spec 14 §24.5 fail-closed: no "ops" default — empty actor is rejected downstream.
        var resolvedActor = string.IsNullOrWhiteSpace(actor) ? string.Empty : actor;

        // OperationConflictException (not ManualReview, or concurrent change) → 409 via GlobalExceptionHandler.
        await mediator.Send(
            new ResolveManualReviewCommand(
                tenant.TenantId, operationId, resolution,
                body.ProviderRef, replayCommandId, resolvedActor,
                reasonCode ?? string.Empty, actorRole ?? string.Empty), ct);

        return Accepted(new { operation_id = operationId, status = body.Resolution.ToLowerInvariant() });
    }

    /// <summary>
    /// Records the customer's response to an alternatives offer (spec 06 §6.1, §11, T11a).
    ///
    /// <c>response</c> must be <c>"waitlist"</c> (→ <c>AlternativesRequired→Waitlisted</c>) or
    /// <c>"decline"</c> (→ <c>AlternativesRequired→FailedFinal</c>). Case-insensitive.
    ///
    /// 202 Accepted on success; 400 if <c>response</c> is invalid; 404 if the operation is
    /// not found; 409 if the operation is not in AlternativesRequired.
    /// </summary>
    [HttpPost("{operationId}/alternatives")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RespondToAlternatives(
        string operationId,
        [FromBody] AlternativesRequest body,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // Map response string (case-insensitive) → enum.
        AlternativesResponse response;
        if (string.Equals(body.Response, "waitlist", StringComparison.OrdinalIgnoreCase))
            response = AlternativesResponse.Waitlist;
        else if (string.Equals(body.Response, "decline", StringComparison.OrdinalIgnoreCase))
            response = AlternativesResponse.Decline;
        else
            return Problem(statusCode: 400, title: "Invalid response",
                detail: $"Response must be 'waitlist' or 'decline', got '{body.Response}'.");

        // Existence check: 404 if not found (also covers tenant mismatch).
        var existing = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);
        if (existing is null)
            return NotFound();

        // OperationConflictException (not AlternativesRequired, or concurrent change) → 409 via GlobalExceptionHandler.
        await mediator.Send(new RespondToAlternativesCommand(tenant.TenantId, operationId, response), ct);

        var status = response == AlternativesResponse.Waitlist ? "waitlisted" : "failed_final";
        return Accepted(new { operation_id = operationId, status });
    }
}

/// <summary>Optional body for POST /resume — caller may supply a pre-generated ReplayCommandId.</summary>
public sealed record ResumeRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("replay_command_id")]
    string? ReplayCommandId);

/// <summary>Body for POST /resolve.</summary>
public sealed record ResolveRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("resolution")]
    string Resolution,
    [property: System.Text.Json.Serialization.JsonPropertyName("provider_ref")]
    string? ProviderRef,
    [property: System.Text.Json.Serialization.JsonPropertyName("replay_command_id")]
    string? ReplayCommandId)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public ResolveRequest() : this(string.Empty, null, null) { }
}

/// <summary>Body for POST /alternatives (T11a).</summary>
public sealed record AlternativesRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("response")]
    string Response)
{
    // Parameterless ctor for model binding.
    public AlternativesRequest() : this(string.Empty) { }
}
