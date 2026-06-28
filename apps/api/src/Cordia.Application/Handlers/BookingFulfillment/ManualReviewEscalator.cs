using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.BookingFulfillment;

/// <summary>
/// Escalates an operation to ManualReview (spec 06 FM#11, §17.2/§17.5, §9 FM#11).
///
/// Protocol:
///   1. Version-guarded transition expectedStatus → ManualReview.  Returns false on rows==0.
///   2. Creates an <see cref="OperationWorkItem"/> (State="open").
///   3. Emits <c>notification.still_confirming.v1</c> outbox row (Inv-8/§17.5: the agent
///      surfaces in_progress, never "booked/charged").
///   4. Writes an <c>operation.manual_review</c> audit event.
///
/// MUST be called INSIDE the caller's unit-of-work transaction (shares the scoped DbContext).
/// <paramref name="expectedStatus"/> MUST be a state with a legal §11 edge to ManualReview
/// (currently Reconciling or CompensationRequired).
/// Register as scoped.
/// </summary>
public sealed class ManualReviewEscalator(
    IBookingOperationRepository opRepo,
    IOperationWorkItemRepository workItemRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo)
{
    /// <summary>
    /// Escalates <paramref name="operationId"/> to ManualReview.
    /// Returns <c>true</c> when the transition won (rows &gt; 0); <c>false</c> when the
    /// row was already concurrently advanced (caller should stop — do not write the rest).
    /// </summary>
    public async Task<bool> EscalateAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        string reason,
        DateTimeOffset slaDeadline,
        CancellationToken ct)
    {
        var rows = await opRepo.TryTransitionStatusAsync(
            tenantId, operationId, expectedVersion,
            expectedStatus, OperationStatus.ManualReview,
            reason: reason, ct: ct);

        if (rows == 0)
            return false;

        var now = DateTimeOffset.UtcNow;

        // Idempotency check (C11): if an open work item already exists for this operation,
        // skip creating a second one.  The unique partial index (uq_open_work_item_per_operation)
        // is the DB backstop, but checking here avoids a spurious constraint violation.
        var existingWorkItem = await workItemRepo.GetOpenByOperationAsync(tenantId, operationId, ct);
        if (existingWorkItem is null)
        {
            await workItemRepo.AddAsync(new OperationWorkItem
            {
                TenantId = tenantId,
                WorkItemId = $"wi_{Guid.NewGuid():N}",
                OperationId = operationId,
                Reason = reason,
                State = "open",
                SlaDeadline = slaDeadline,
                CreatedAt = now,
                Version = 0,
            }, ct);
        }

        // Inv-8 / §17.5: "still confirming" notification — the operation is in_progress,
        // not booked or charged.  Emitted atomically with the ManualReview transition.
        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            OperationId = operationId,
            MessageType = "notification.still_confirming.v1",
            CorrelationId = operationId,
            Payload = JsonSerializer.Serialize(new
            {
                operation_id = operationId,
                tenant_id = tenantId,
            }),
            OccurredAt = now,
        }, ct);

        await auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = "operation.manual_review",
            Actor = "system",
            SubjectRef = operationId,
            CorrelationId = operationId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(new { reason }),
            OccurredAt = now,
        }, ct);

        return true;
    }
}
