using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Preorder;

/// <summary>
/// Shared helpers for the pre-order saga step handlers: parsing the immutable
/// <see cref="PreorderReference"/> off the saga row, computing the deposit/order total, and building
/// the per-step audit row. Keeps the individual step handlers thin and consistent.
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
    /// Total order amount (Σ per-unit captured price × quantity, minor units) and the currency
    /// (taken from the first line; the saga is single-currency per the captured reference).
    /// </summary>
    public static (long AmountMinor, string Currency) Total(PreorderReference reference)
    {
        long total = 0;
        var currency = string.Empty;
        foreach (var line in reference.Lines ?? [])
        {
            total += line.CapturedPriceAmountMinor * line.Quantity;
            if (currency.Length == 0)
                currency = line.CapturedPriceCurrency;
        }
        return (total, currency);
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

    /// <summary>Builds a system-actor audit row for a saga step/transition.</summary>
    public static AuditEvent Audit(
        string tenantId, string eventType, string sagaId, string operationId,
        DateTimeOffset now, object? metadata = null)
        => new()
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = sagaId,
            CorrelationId = sagaId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(metadata ?? new { saga_id = sagaId }),
            OccurredAt = now,
        };
}
