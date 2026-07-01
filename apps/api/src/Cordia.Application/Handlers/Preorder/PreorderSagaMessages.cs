using System.Text.Json;
using Cordia.Domain.Entities;

namespace Cordia.Application.Handlers.Preorder;

/// <summary>
/// The <c>preorder.commands.*</c> message-type catalog and the step-envelope builder for the
/// pre-order saga (spec 11 §5.3). Every saga step, compensation, and the reconcile/timeout signal
/// is a distinct message type on the <c>preorder.commands</c> lane (consumer
/// <c>"preorder.processor"</c>). Step envelopes carry ONLY opaque ids (saga + operation) — never
/// card data, PII, or pre-order content (Inv-12); the handler reloads the saga row (which holds the
/// immutable <c>PreorderReference</c> and the step checkpoints) to do its work.
/// </summary>
public static class PreorderSagaMessages
{
    /// <summary>Inbox consumer name; all saga step handlers dedup under this (Inv-5).</summary>
    public const string ConsumerName = "preorder.processor";

    /// <summary>Destination queue wire name (routes to the preorder.processor consumer).</summary>
    public const string QueueWireName = "preorder.commands";

    // Forward steps. The saga is items-first, then delegates the whole booking (capacity/payment/
    // create) to the spec-06 engine (§5.3, divergence #2), so it no longer has its own hold-capacity /
    // authorize-payment / commit-booking steps — those were removed. It waits in CommittingBooking for
    // the 06 booking-outcome signal, then binds the pre-order (CommitPreorder).
    public const string Start = "preorder.commands.start.v1";
    public const string ReserveItems = "preorder.commands.reserve_items.v1";
    public const string CommitPreorder = "preorder.commands.commit_preorder.v1";

    // Compensation steps. Items reservation is the only thing the saga owns to roll back
    // (CompensateItems); CompensateBooking is a defensive escalation to ManualReview for the (now
    // unreachable) post-commit-failure state — the committed booking is preserved for a human (§21.4).
    public const string CompensateItems = "preorder.commands.compensate_items.v1";
    public const string CompensateBooking = "preorder.commands.compensate_booking.v1";

    // 06 → saga booking-outcome signals. The spec-06 operation engine owns capacity (07),
    // payment (09), and provider-create (06); when a pre-order operation reaches a terminal
    // outcome it signals the saga (instead of emitting a customer-facing notification), which
    // then commits the pre-order or compensates the reserved items (spec 11 §5.3, Inv-8).
    public const string BookingCommitted = "preorder.commands.booking_committed.v1";
    public const string BookingFailed = "preorder.commands.booking_failed.v1";

    // Recovery / timeout signal.
    public const string Reconcile = "preorder.commands.reconcile.v1";

    // Customer-facing outcome message types owned by spec 10 (emitted by the 06 engine for a normal
    // booking). Named here so the pre-order gate below has a single source of truth.
    public const string BookingConfirmedNotification = "notification.booking_confirmed.v1";
    public const string BookingFailedNotification = "notification.booking_failed.v1";

    /// <summary>
    /// Booking-outcome message type for an operation reaching a terminal <c>Booked</c> state. For a
    /// pre-order operation (§5.3) the 06 engine signals the saga
    /// (<c>preorder.commands.booking_committed.v1</c>) — the saga then emits the single combined
    /// customer outcome (Inv-8) — instead of notifying the customer directly. A normal operation emits
    /// the customer confirmation. Every 06 emit site branches through this to stay consistent.
    /// </summary>
    public static string ConfirmedOutcomeType(bool isPreorder)
        => isPreorder ? BookingCommitted : BookingConfirmedNotification;

    /// <summary>
    /// Booking-outcome message type for an operation reaching a terminal failure. For a pre-order
    /// operation the 06 engine signals the saga (<c>preorder.commands.booking_failed.v1</c>) so it can
    /// compensate the reserved items and emit the one combined failure outcome; a normal operation
    /// emits the customer failure notification.
    /// </summary>
    public static string FailedOutcomeType(bool isPreorder)
        => isPreorder ? BookingFailed : BookingFailedNotification;

    /// <summary>
    /// Builds an outbox message handing the saga off to its next step (or a compensation step). The
    /// payload carries the saga id (and operation id for correlation) only; the consuming handler
    /// reloads the saga to read the reference + checkpoints.
    /// </summary>
    public static OutboxMessage StepMessage(
        string tenantId,
        string messageType,
        string sagaId,
        string operationId,
        DateTimeOffset now,
        string? correlationId = null)
        => new()
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = messageType,
            CorrelationId = correlationId ?? operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { saga_id = sagaId, operation_id = operationId }),
            OccurredAt = now,
        };
}
