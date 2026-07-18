using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;

/// <summary>
/// Handles a provider delivery-receipt webhook (spec 10 §6.1, §12 FM#8, FM#11).
///
/// State-machine edges processed here (§18.3):
///   Accepted → Delivered       (positive delivery receipt)
///   Accepted → FailedPermanent (bounce / post-acceptance undelivered)
///
/// Idempotency key: (notification_id, provider_message_id). A receipt carrying a
/// provider_message_id that does not match the stored provider_ref is a stale or
/// foreign receipt — logged and acknowledged without a state change (Mismatch).
///
/// Race handling (FM#11): uses version-guarded TryTransitionStateAsync / MarkFailedAsync.
/// rows==0 means a concurrent update beat us; we reload once and, if the row is now in
/// the expected terminal state, return AlreadyTerminal. We NEVER force the transition.
///
/// Absence of Accepted-state acceptance never silently becomes Delivered (§18.3):
/// any state other than Accepted or a terminal returns OutOfOrder without a transition.
///
/// HTTP handler: owns its own transaction via IUnitOfWork (same pattern as
/// SubmitConfirmedIntentCommandHandler; inbox consumers must not open their own txn).
/// </summary>
public sealed class RecordDeliveryReceiptCommandHandler(
    INotificationRepository repo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow,
    ILogger<RecordDeliveryReceiptCommandHandler> logger,
    Metrics metrics)
    : IRequestHandler<RecordDeliveryReceiptCommand, ReceiptOutcome>
{
    private static readonly HashSet<NotificationState> _terminalStates = new()
    {
        NotificationState.Delivered,
        NotificationState.FailedPermanent,
        NotificationState.SuppressedDuplicate,
        NotificationState.InvalidRecipient,
    };

    public async Task<ReceiptOutcome> Handle(
        RecordDeliveryReceiptCommand command,
        CancellationToken ct)
    {
        ReceiptOutcome outcome = ReceiptOutcome.NotFound;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // 1. Load current row.
            var n = await repo.GetAsync(command.TenantId, command.NotificationId, ct2);
            if (n is null)
            {
                outcome = ReceiptOutcome.NotFound;
                return;
            }

            // 2. Idempotency on (notification_id, provider_message_id).
            // If the stored provider_ref does not match, this is a stale or foreign receipt.
            // Log a warning and ack without touching state — never mutate on a mismatch.
            if (n.ProviderRef != command.ProviderMessageId)
            {
                logger.LogWarning(
                    "RecordDeliveryReceipt: provider_message_id mismatch for notification {NotificationId}. " +
                    "Stored provider_ref={StoredRef}; receipt provider_message_id={ReceiptRef}. Ignoring.",
                    command.NotificationId, n.ProviderRef, command.ProviderMessageId);
                outcome = ReceiptOutcome.Mismatch;
                return;
            }

            // 3. Already terminal — idempotent ack.
            if (_terminalStates.Contains(n.State))
            {
                outcome = ReceiptOutcome.AlreadyTerminal;
                return;
            }

            // 4. Only Accepted is a valid source state for a delivery receipt.
            if (n.State != NotificationState.Accepted)
            {
                // Receipt arrived before Accepted (e.g. Sending, Pending, FailedTransient).
                // Absence of acceptance must never silently become Delivered (§18.3).
                logger.LogWarning(
                    "RecordDeliveryReceipt: out-of-order receipt for notification {NotificationId}. " +
                    "Current state={State}; expected Accepted. No state change.",
                    command.NotificationId, n.State);
                outcome = ReceiptOutcome.OutOfOrder;
                return;
            }

            // 5. Apply the transition (version-guarded).
            var now = DateTimeOffset.UtcNow;
            if (command.Status == DeliveryReceiptStatus.Delivered)
            {
                var rows = await repo.TryTransitionStateAsync(
                    command.TenantId, command.NotificationId,
                    n.Version, NotificationState.Accepted, NotificationState.Delivered,
                    ct2);

                if (rows == 0)
                {
                    // FM#11: lost race — reload and check if already terminal.
                    var reloaded = await repo.GetAsync(command.TenantId, command.NotificationId, ct2);
                    if (reloaded?.State == NotificationState.Delivered)
                    {
                        outcome = ReceiptOutcome.AlreadyTerminal;
                        return;
                    }

                    logger.LogWarning(
                        "RecordDeliveryReceipt: lost race settling Accepted→Delivered for {NotificationId}. " +
                        "Reloaded state={State}. Returning OutOfOrder.",
                        command.NotificationId, reloaded?.State);
                    outcome = ReceiptOutcome.OutOfOrder;
                    return;
                }

                // rows == 1 — success. Emit audit event.
                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "notification.delivered",
                    Actor = "provider",
                    SubjectRef = command.NotificationId,
                    CorrelationId = command.NotificationId,
                    OperationId = n.OperationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        notification_id = command.NotificationId,
                        channel = n.Channel.ToString(),
                        provider_ref = n.ProviderRef,
                        status = "delivered",
                    }),
                    OccurredAt = now,
                }, ct2);
                metrics.NotificationDeliveredTotal.Add(1);

                outcome = ReceiptOutcome.Applied;
            }
            else // Bounced
            {
                var rows = await repo.MarkFailedAsync(
                    command.TenantId, command.NotificationId,
                    n.Version, NotificationState.Accepted, NotificationState.FailedPermanent,
                    "bounce",
                    ct2);

                if (rows == 0)
                {
                    // FM#11: lost race — reload and check if already terminal.
                    var reloaded = await repo.GetAsync(command.TenantId, command.NotificationId, ct2);
                    if (reloaded?.State == NotificationState.FailedPermanent)
                    {
                        outcome = ReceiptOutcome.AlreadyTerminal;
                        return;
                    }

                    logger.LogWarning(
                        "RecordDeliveryReceipt: lost race settling Accepted→FailedPermanent (bounce) for {NotificationId}. " +
                        "Reloaded state={State}. Returning OutOfOrder.",
                        command.NotificationId, reloaded?.State);
                    outcome = ReceiptOutcome.OutOfOrder;
                    return;
                }

                // rows == 1 — success. Emit audit event.
                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "notification.bounced",
                    Actor = "provider",
                    SubjectRef = command.NotificationId,
                    CorrelationId = command.NotificationId,
                    OperationId = n.OperationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        notification_id = command.NotificationId,
                        channel = n.Channel.ToString(),
                        provider_ref = n.ProviderRef,
                        failure_category = "bounce",
                        status = "bounced",
                    }),
                    OccurredAt = now,
                }, ct2);
                metrics.NotificationBouncedTotal.Add(1);

                outcome = ReceiptOutcome.Applied;
            }
        }, ct);

        return outcome;
    }
}
