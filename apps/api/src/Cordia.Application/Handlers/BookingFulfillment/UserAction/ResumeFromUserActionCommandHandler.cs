using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.UserAction;

/// <summary>
/// Handles the "user action completed" event (spec 06 §6.1 AwaitingUserAction → Accepted).
///
/// Runs INSIDE the InboxDeduplicationBehavior transaction — do NOT open a new IUnitOfWork.
/// Guard: if op.Status != AwaitingUserAction → return Unit (wrong state; inbox row committed).
/// </summary>
public sealed class ResumeFromUserActionCommandHandler(
    IBookingOperationRepository opRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo)
    : IRequestHandler<ResumeFromUserActionCommand, Unit>
{
    public async Task<Unit> Handle(ResumeFromUserActionCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        var op = await opRepo.GetAsync(operationId, ct);
        if (op is null || op.TenantId != tenantId || op.Status != OperationStatus.AwaitingUserAction)
            return Unit.Value; // wrong state or already handled

        var now = DateTimeOffset.UtcNow;

        // AwaitingUserAction → Accepted (version-guarded; Accepted re-enters validate loop).
        var rows = await opRepo.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.AwaitingUserAction, OperationStatus.Accepted,
            reason: "user_action_completed", ct: ct);

        if (rows == 0)
            return Unit.Value; // lost race

        // Emit booking.command.requested.v1 so the driver re-validates.
        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            OperationId = operationId,
            MessageType = "booking.command.requested.v1",
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
            EventType = "operation.user_action_completed",
            Actor = "system",
            SubjectRef = operationId,
            CorrelationId = operationId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(new { reason = "user_action_completed" }),
            OccurredAt = now,
        }, ct);

        return Unit.Value;
    }
}
