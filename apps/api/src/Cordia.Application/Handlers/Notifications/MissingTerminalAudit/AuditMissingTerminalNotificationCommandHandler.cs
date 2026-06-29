using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications.MissingTerminalAudit;

/// <summary>
/// Raises an <c>operations_work_items</c> row + audit event when a terminal booking operation
/// has no accepted/delivered notification (spec 10 §25.4 / FM#12 / Inv-14, T5b).
///
/// Inv-14: does NOT mutate <c>booking_operations</c> — the booking stays confirmed regardless
/// of notification failure.
/// </summary>
public sealed class AuditMissingTerminalNotificationCommandHandler(
    IOperationWorkItemRepository workItemRepo,
    INotificationRepository notifications,
    IAuditEventRepository audit,
    IUnitOfWork uow,
    ILogger<AuditMissingTerminalNotificationCommandHandler> logger)
    : IRequestHandler<AuditMissingTerminalNotificationCommand, bool>
{
    /// <summary>Maps the terminal operation status to the expected notification event type (F4).</summary>
    private static NotificationEvent ExpectedEventFor(OperationStatus status) => status switch
    {
        OperationStatus.Booked => NotificationEvent.BookingConfirmed,
        _ => NotificationEvent.FinalFailure, // FailedFinal, Rejected, Expired
    };

    public async Task<bool> Handle(
        AuditMissingTerminalNotificationCommand request,
        CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId;
        var operationId = request.OperationId;
        var expectedEvent = ExpectedEventFor(request.TerminalStatus);

        var created = false;

        await uow.ExecuteInTransactionAsync(async ct =>
        {
            // 1. Idempotency: if an open work item already exists for this operation, do nothing.
            //    Also avoids hitting the uq_open_work_item_per_operation unique partial index.
            if (await workItemRepo.GetOpenByOperationAsync(tenantId, operationId, ct) is not null)
            {
                logger.LogDebug(
                    "MissingNotificationAudit: open work item already exists for " +
                    "tenant={TenantId} operation_id={OperationId}; skipping",
                    tenantId, operationId);
                return;
            }

            // 2. Defensive re-check: the specific terminal notification may have been accepted
            //    between the list sweep and this handler — no alert needed.
            if (await notifications.HasAcceptedTerminalNotificationAsync(tenantId, operationId, expectedEvent, ct))
            {
                logger.LogDebug(
                    "MissingNotificationAudit: {ExpectedEvent} notification already accepted for " +
                    "tenant={TenantId} operation_id={OperationId}; skipping",
                    expectedEvent, tenantId, operationId);
                return;
            }

            // 3. Create the work item + audit (race-safe: ON CONFLICT DO NOTHING, F8).
            var now = DateTimeOffset.UtcNow;

            var inserted = await workItemRepo.TryAddOpenAsync(new OperationWorkItem
            {
                TenantId = tenantId,
                WorkItemId = $"wi_{Guid.NewGuid():N}",
                OperationId = operationId,
                Reason = "missing_notification",
                State = "open",
                SlaDeadline = now.AddHours(1),
                CreatedAt = now,
                Version = 0,
            }, ct);

            if (!inserted)
            {
                // Another worker concurrently inserted the open work item — benign no-op.
                logger.LogDebug(
                    "MissingNotificationAudit: concurrent insert won for " +
                    "tenant={TenantId} operation_id={OperationId}; skipping audit",
                    tenantId, operationId);
                return;
            }

            await audit.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "notification.missing_terminal",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    reason = "missing_notification",
                }),
                OccurredAt = now,
            }, ct);

            created = true;

            logger.LogInformation(
                "MissingNotificationAudit: raised work item + audit for " +
                "tenant={TenantId} operation_id={OperationId}",
                tenantId, operationId);
        }, cancellationToken);

        return created;
    }
}
