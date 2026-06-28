using MediatR;

namespace Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;

/// <summary>Delivery outcome reported by the provider via webhook (spec 10 §6.1, §12 FM#8, FM#11).</summary>
public enum DeliveryReceiptStatus { Delivered, Bounced }

/// <summary>Result of processing a delivery receipt.</summary>
public enum ReceiptOutcome
{
    /// <summary>Transition applied successfully (Accepted → Delivered or Accepted → FailedPermanent).</summary>
    Applied,

    /// <summary>Notification is already in a terminal state (idempotent ack).</summary>
    AlreadyTerminal,

    /// <summary>No notification row found for (tenantId, notificationId).</summary>
    NotFound,

    /// <summary>Receipt provider_message_id does not match the stored provider_ref (stale/foreign receipt).</summary>
    Mismatch,

    /// <summary>Receipt arrived before Accepted (e.g. still Sending/Pending) — out-of-order; no state change.</summary>
    OutOfOrder,
}

/// <summary>
/// Records a delivery receipt from the notification provider (spec 10 §6.1, RecordDeliveryReceipt).
/// Accepted → Delivered (positive receipt) or Accepted → FailedPermanent (bounce). Idempotent on
/// (notification_id, provider_message_id) per §22.1 OCC; race handled via reload (FM#11).
/// </summary>
public sealed record RecordDeliveryReceiptCommand(
    string TenantId,
    string NotificationId,
    string ProviderMessageId,
    DeliveryReceiptStatus Status)
    : IRequest<ReceiptOutcome>;
