using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Preorder;

/// <summary>
/// Shared helpers for the pre-order saga step handlers: parsing the immutable
/// <see cref="PreorderReference"/> off the saga row, building the combined outcome / neutral in-review
/// notifications and the per-step audit row, and scheduling the reconcile timeout. Keeps the individual
/// step handlers thin and consistent.
/// </summary>
public static class PreorderSagaSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Combined failure outcome (Inv-8): emitted exactly once when a saga reaches a failure terminal
    /// (FailedFinal / ManualReview). Reuses the spec-10 notification lane (FinalFailure).
    /// </summary>
    public const string OutcomeFailedEventType = "notification.booking_failed.v1";

    /// <summary>Builds the single combined failure-outcome notification for a terminal-failed saga.</summary>
    public static OutboxMessage FailureOutcome(
        string tenantId, string sagaId, string operationId, string reason, DateTimeOffset now, string? correlationId = null)
        => new()
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = OutcomeFailedEventType,
            CorrelationId = correlationId ?? operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { saga_id = sagaId, operation_id = operationId, reason }),
            OccurredAt = now,
        };

    /// <summary>
    /// Outcome event for an operator cancel of a ManualReview-parked pre-order (§21.4). ManualReview
    /// is reached only AFTER 06 committed the booking, so the cancel unwinds the FOOD ORDER only —
    /// the table reservation stays confirmed. The customer must NOT receive the "booking failed"
    /// template for a booking they still hold.
    /// </summary>
    public const string PreorderCancelledEventType = "notification.preorder_cancelled.v1";

    /// <summary>Builds the terminal "pre-order cancelled, your booking stands" outcome
    /// (<see cref="PreorderCancelledEventType"/>).</summary>
    public static OutboxMessage PreorderCancelledOutcome(
        string tenantId, string sagaId, string operationId, string reason, DateTimeOffset now, string? correlationId = null)
        => new()
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = PreorderCancelledEventType,
            CorrelationId = correlationId ?? operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { saga_id = sagaId, operation_id = operationId, reason }),
            OccurredAt = now,
        };

    /// <summary>
    /// Builds a NEUTRAL "under review / still confirming" customer notice for a saga parked in
    /// <c>ManualReview</c> (spec 10 <c>NotificationEvent.StillPending</c>). This is deliberately NOT a
    /// terminal outcome — it keeps the customer informed without pre-empting the single combined outcome
    /// (Inv-8) that the terminal convert (confirmed) or cancel (failed) resolution owns.
    /// </summary>
    public static OutboxMessage InReviewNotice(
        string tenantId, string sagaId, string operationId, DateTimeOffset now, string? correlationId = null)
        => new()
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = PreorderSagaMessages.InReviewNotification,
            CorrelationId = correlationId ?? operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { saga_id = sagaId, operation_id = operationId, reason = "under_review" }),
            OccurredAt = now,
        };

    /// <summary>Parses the saga's stored <c>preorder_reference</c> jsonb, or <c>null</c> if unparseable.</summary>
    public static PreorderReference? ParseReference(string referenceJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PreorderReference>(referenceJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-step reconcile timeout window (spec 11 §5.3): how long a saga may sit in an in-flight step
    /// before the safety-net reconcile fires. The <see cref="PreorderSagaState.CommittingBooking"/>
    /// window is the longest because that step waits on the whole spec-06 engine (capacity 07 →
    /// payment 09 → provider-create); the local item-reservation / pre-order-bind steps are short.
    /// A generous window keeps this a backstop, not the primary path — the 06→saga signal normally
    /// advances the saga well before the timeout.
    /// </summary>
    public static TimeSpan TimeoutWindow(PreorderSagaState state) => state switch
    {
        PreorderSagaState.ReservingItems => TimeSpan.FromMinutes(2),
        PreorderSagaState.CommittingBooking => TimeSpan.FromMinutes(10),
        PreorderSagaState.CommittingPreorder => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Schedules the per-step reconcile timeout for an in-flight saga step (spec 11 §5.3, divergence
    /// #3). Writes a <c>preorder.commands.reconcile.v1</c> <see cref="ScheduledJob"/> due at
    /// <c>now + <see cref="TimeoutWindow"/>(state)</c> on the <c>preorder.commands</c> lane; when it
    /// fires, <c>ReconcilePreorderSagaCommandHandler</c> is state-driven and idempotent, so it re-drives
    /// the CURRENT step or settles if the saga already advanced (no cancellation needed).
    ///
    /// <para>MUST be called inside the caller's ambient (inbox) transaction so the job insert commits
    /// atomically with the state advance. The job id is deterministic per (saga, state)
    /// (<c>sched_preorder_{sagaId}_{state}</c>), so duplicate delivery / re-entry into the same step
    /// does not stack duplicate timers — a pre-existing row (any status) short-circuits the insert.</para>
    /// </summary>
    public static async Task ScheduleStepTimeoutAsync(
        IScheduledJobRepository jobs,
        string tenantId,
        string sagaId,
        string operationId,
        PreorderSagaState state,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var jobId = $"sched_preorder_{sagaId}_{state.ToString().ToLowerInvariant()}";

        // Deterministic dedup: one timeout per (saga, state). ExistsAsync matches any status, so a
        // re-drive of the same step (or an already-fired-and-Done timer) never re-schedules.
        if (await jobs.ExistsAsync(tenantId, jobId, ct))
            return;

        await jobs.AddAsync(new ScheduledJob
        {
            TenantId = tenantId,
            JobId = jobId,
            JobType = PreorderSagaMessages.Reconcile, // "preorder.commands.reconcile.v1"
            RunAfter = now + TimeoutWindow(state),
            DestinationQueue = PreorderSagaMessages.QueueWireName, // "preorder.commands"
            Status = ScheduledJobStatus.Pending,
            Attempt = 0,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { saga_id = sagaId, operation_id = operationId }),
        }, ct);
    }

    /// <summary>
    /// Builds an audit row for a saga step/transition. <paramref name="actor"/> defaults to
    /// <c>"system"</c> for automated steps; pass the operator identity for human-initiated resolutions
    /// (convert/cancel) so the audit trail attributes the action to the real actor.
    /// </summary>
    public static AuditEvent Audit(
        string tenantId, string eventType, string sagaId, string operationId,
        DateTimeOffset now, object? metadata = null, string actor = "system")
        => new()
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = actor,
            SubjectRef = sagaId,
            CorrelationId = sagaId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(metadata ?? new { saga_id = sagaId }),
            OccurredAt = now,
        };
}
