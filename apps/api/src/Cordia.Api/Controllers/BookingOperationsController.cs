using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Application.Handlers.BookingFulfillment.Cancellation;
using Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;
using Cordia.Application.Handlers.BookingOperations.GetOperationStatus;
using Cordia.Application.Handlers.Console.ListOpenWorkItems;
using Cordia.Application.Handlers.Console.ListOperations;
using Cordia.Application.Handlers.Notifications.ListNotifications;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cordia.Api.Controllers;

/// <summary>
/// REST endpoints for booking operations (spec 04 §3, spec 06 §15.5, §17.4, T11a).
///
/// GET  /api/booking-operations/{operationId}                    → 200 | 404
/// GET  /api/booking-operations/{operationId}/notifications      → 200 | 403
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
public class BookingOperationsController(
    IMediator mediator,
    ITenantContext tenant,
    IActorContext actorContext,
    IOptions<AssertedActorOptions> assertedActor) : ControllerBase
{
    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultListLimit = 100;

    /// <summary>
    /// Lists booking operations for the console's Queue / Operations board, newest activity first.
    ///
    /// <paramref name="status"/> accepts repeated or comma-separated snake_case values from
    /// the ratified 16-value set (D1, owner spec 06); an unknown value is a 400 rather than
    /// being ignored, so a typo cannot silently widen the result to "everything".
    /// <paramref name="needsAttention"/> is shorthand for the states that are waiting on a
    /// person or have stopped making progress on their own.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ListOperationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ListOperationsResponse>> List(
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "needs_attention")] bool? needsAttention,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var requested = (status ?? [])
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        var parsed = new List<OperationStatus>(requested.Length);
        var unknown = new List<string>();
        foreach (var value in requested)
        {
            if (OperationStatusNames.TryParse(value, out var operationStatus))
                parsed.Add(operationStatus);
            else
                unknown.Add(value);
        }

        if (unknown.Count > 0)
        {
            ModelState.AddModelError(
                "status",
                $"Unknown operation status: {string.Join(", ", unknown)}. Valid values: {string.Join(", ", OperationStatusNames.All)}.");
            return ValidationProblem(ModelState);
        }

        if (needsAttention == true)
            parsed.AddRange(ListOperationsQueryHandler.NeedsAttention);

        var operations = await mediator.Send(
            new ListOperationsQuery(tenant.TenantId, parsed.Distinct().ToArray(), limit ?? DefaultListLimit),
            ct);

        return Ok(new ListOperationsResponse(
            operations.Select(o => new OperationSummaryView(
                o.OperationId,
                o.BookingId,
                o.IntentId,
                o.IntentRevision,
                EnumWireNames.ToWireName(o.OperationType),
                OperationStatusNames.ToWireName(o.Status),
                o.StatusReason,
                o.IsPreorder,
                o.CorrelationId,
                o.CreatedAt,
                o.UpdatedAt)).ToList()));
    }

    /// <summary>
    /// Lists the OPEN manual-recovery work items for this tenant, most overdue first.
    ///
    /// <para>The queue an operator works through. Every path that gives up on an operation — an
    /// ambiguous provider cancel, an exhausted retry budget, a poison message, an SLA breach, a
    /// parked pre-order whose booking already committed — durably opens a work item carrying a
    /// reason and an SLA deadline. Until this endpoint there was no way to read them: the only
    /// readers were two cross-tenant aggregates feeding metrics gauges, so the manual-review depth
    /// could be seen rising without anyone being able to see what was in it.</para>
    ///
    /// <para>Ordered by SLA deadline rather than creation time, because different reasons carry
    /// different deadlines and "most overdue" is the order the work should be done in.</para>
    /// </summary>
    [HttpGet("work-items")]
    [ProducesResponseType(typeof(ListWorkItemsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ListWorkItemsResponse>> ListWorkItems(
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var items = await mediator.Send(
            new ListOpenWorkItemsQuery(tenant.TenantId, limit ?? DefaultListLimit), ct);

        return Ok(new ListWorkItemsResponse(
            items.Select(w => new OperationWorkItemView(
                w.WorkItemId,
                w.OperationId,
                w.Reason,
                w.SlaDeadline,
                w.Assignee,
                w.CreatedAt,
                w.OperationStatus is null ? null : OperationStatusNames.ToWireName(w.OperationStatus.Value))).ToList()));
    }

    /// <summary>
    /// Delivery state of the notifications raised for one operation (#83, spec 10) — the console's
    /// read for a request that has not become a booking yet. Same contract as
    /// <c>GET /api/Bookings/{bookingId}/notifications</c>.
    /// </summary>
    [HttpGet("{operationId}/notifications")]
    [ProducesResponseType(typeof(ListNotificationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ListNotificationsResponse>> ListNotifications(
        string operationId,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(actorContext, assertedActor.Value.AllowAssertedStaffRole, "notification.list_read");

        return Ok(await mediator.Send(new ListNotificationsQuery(tenant.TenantId, BookingId: null, operationId), ct));
    }

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
    /// TODO(#79-followup): surface cancellation_pending on the status projection too.
    /// Deliberately still deferred: OperationStatusView is a generated Pydantic contract, so a
    /// dedicated field is a cross-language regen rather than a controller change, and
    /// GetOperationStatusQuery already tells the customer through CustomerMessage. What #53
    /// fixed is narrower and is done: THIS endpoint no longer claims a cancellation is pending
    /// when the operation has already reached a terminal state.
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
            return TerminalConflict(operationId);

        // The read above is a SNAPSHOT. Between it and the write below the workflow driver can
        // legally commit a terminal transition (Finalizing→Booked, AlternativesRequired→FailedFinal,
        // …), so the guard alone does not make the answer true — only usually true.
        //
        // RequestCancellationCommand already reports what actually happened: its repository filter
        // is `CancellationRequestedAt == null AND NOT terminal`, so it returns false when it matched
        // no row. This used to discard that and hardcode cancellation_pending = true, telling the
        // caller a cancellation was pending on an operation that had already finished (#53, Inv-16).
        var newlySet = await mediator.Send(new RequestCancellationCommand(tenant.TenantId, operationId), ct);

        if (!newlySet)
        {
            // false is ambiguous by design — "already pending" OR "raced into terminal". Those need
            // different answers, so re-read rather than guess.
            var fresh = await mediator.Send(new GetOperationStatusQuery(tenant.TenantId, operationId), ct);

            if (fresh is null)
                return NotFound();

            if (fresh.Terminal)
                return TerminalConflict(operationId);

            // Non-terminal and not newly set ⇒ a cancellation was already pending. 202 with
            // cancellation_pending = true is both idempotent and true (§15.5).
        }

        return Accepted(new { operation_id = operationId, cancellation_pending = true });
    }

    /// <summary>
    /// The 409 body for an already-terminal operation. Shared by the up-front guard and the
    /// post-write re-read so a caught race is indistinguishable from a caught snapshot — a caller
    /// must not be able to tell which check fired.
    /// </summary>
    private ConflictObjectResult TerminalConflict(string operationId) =>
        Conflict(new
        {
            operation_id = operationId,
            detail = "Terminal operations cannot be cancelled via this endpoint. " +
                     "Post-booking cancellation is a separate flow (spec 08).",
        });

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
        // Spec 14 §24.5 fail-closed: a verified session wins over the shim; no "ops" default —
        // empty actor is rejected downstream.
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        // OperationConflictException (not ManualReview, or concurrent change) → 409 via GlobalExceptionHandler.
        await mediator.Send(
            new ResumeWorkflowStepCommand(
                tenant.TenantId, operationId, replayCommandId, resolvedActor,
                reasonCode ?? string.Empty, resolvedRole), ct);

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
        // Spec 14 §24.5 fail-closed: a verified session wins over the shim; no "ops" default —
        // empty actor is rejected downstream.
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        // OperationConflictException (not ManualReview, or concurrent change) → 409 via GlobalExceptionHandler.
        await mediator.Send(
            new ResolveManualReviewCommand(
                tenant.TenantId, operationId, resolution,
                body.ProviderRef, replayCommandId, resolvedActor,
                reasonCode ?? string.Empty, resolvedRole), ct);

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

/// <summary>One row of the console's operations board.</summary>
public sealed record OperationSummaryView(
    [property: System.Text.Json.Serialization.JsonPropertyName("operation_id")] string OperationId,
    [property: System.Text.Json.Serialization.JsonPropertyName("booking_id")] string? BookingId,
    [property: System.Text.Json.Serialization.JsonPropertyName("intent_id")] string IntentId,
    [property: System.Text.Json.Serialization.JsonPropertyName("intent_revision")] int IntentRevision,
    [property: System.Text.Json.Serialization.JsonPropertyName("operation_type")] string OperationType,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("status_reason")] string? StatusReason,
    [property: System.Text.Json.Serialization.JsonPropertyName("is_preorder")] bool IsPreorder,
    [property: System.Text.Json.Serialization.JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: System.Text.Json.Serialization.JsonPropertyName("created_at")] System.DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("updated_at")] System.DateTimeOffset UpdatedAt);

public sealed record ListOperationsResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("operations")]
    IReadOnlyList<OperationSummaryView> Operations);

/// <summary>
/// An open manual-recovery work item (spec 06 §17.5, FM#11).
///
/// <c>operation_status</c> is the operation's status RIGHT NOW, not when the item was raised — a
/// work item stays open until a person closes it, so the operation may have moved or finished since.
/// It is null when the linked operation is not visible to this tenant.
/// </summary>
public sealed record OperationWorkItemView(
    [property: System.Text.Json.Serialization.JsonPropertyName("work_item_id")] string WorkItemId,
    [property: System.Text.Json.Serialization.JsonPropertyName("operation_id")] string OperationId,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string Reason,
    [property: System.Text.Json.Serialization.JsonPropertyName("sla_deadline")] System.DateTimeOffset SlaDeadline,
    [property: System.Text.Json.Serialization.JsonPropertyName("assignee")] string? Assignee,
    [property: System.Text.Json.Serialization.JsonPropertyName("created_at")] System.DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("operation_status")] string? OperationStatus);

public sealed record ListWorkItemsResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("work_items")]
    IReadOnlyList<OperationWorkItemView> WorkItems);
