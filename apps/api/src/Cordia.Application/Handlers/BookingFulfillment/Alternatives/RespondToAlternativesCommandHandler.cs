using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Alternatives;

/// <summary>
/// Handles the customer's response to an <c>AlternativesRequired</c> operation (spec 06 §6.1, T11a).
///
/// <c>Waitlist</c> → version-guarded <c>AlternativesRequired→Waitlisted</c> + outbox
///   <c>notification.waitlisted.v1</c> + audit <c>operation.waitlisted</c>.
///
/// <c>Decline</c>  → version-guarded <c>AlternativesRequired→FailedFinal</c> + outbox
///   <c>notification.booking_failed.v1</c> + audit <c>operation.failed_final</c>.
///
/// Guard: if the operation is not in <c>AlternativesRequired</c> (incl. not found / tenant mismatch),
/// throws <see cref="OperationConflictException"/> → HTTP 409.
/// </summary>
public sealed class RespondToAlternativesCommandHandler(
    IBookingOperationRepository opRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<RespondToAlternativesCommand, Unit>
{
    public async Task<Unit> Handle(RespondToAlternativesCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            throw new OperationConflictException(command.OperationId, "Operation not found or tenant mismatch.");

        if (op.Status != OperationStatus.AlternativesRequired)
            throw new OperationConflictException(
                command.OperationId,
                $"Operation is in status '{op.Status}', expected AlternativesRequired.");

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Re-read inside transaction for version-guarded transition.
            var current = await opRepo.GetAsync(command.OperationId, ct2);
            if (current is null || current.TenantId != command.TenantId)
                throw new OperationConflictException(command.OperationId, "Operation not found or tenant mismatch.");

            if (current.Status != OperationStatus.AlternativesRequired)
                throw new OperationConflictException(
                    command.OperationId,
                    $"Operation is in status '{current.Status}', expected AlternativesRequired.");

            var now = DateTimeOffset.UtcNow;

            if (command.Response == AlternativesResponse.Waitlist)
            {
                var rows = await opRepo.TryTransitionStatusAsync(
                    command.TenantId, command.OperationId, current.Version,
                    OperationStatus.AlternativesRequired, OperationStatus.Waitlisted,
                    reason: "waitlisted", ct: ct2);

                if (rows == 0)
                    throw new OperationConflictException(
                        command.OperationId, "Concurrent update — retry.");

                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = "notification.waitlisted.v1",
                    CorrelationId = command.OperationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        tenant_id = command.TenantId,
                    }),
                    OccurredAt = now,
                }, ct2);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "operation.waitlisted",
                    Actor = "system",
                    SubjectRef = command.OperationId,
                    CorrelationId = command.OperationId,
                    OperationId = command.OperationId,
                    Metadata = JsonSerializer.Serialize(new { reason = "waitlisted" }),
                    OccurredAt = now,
                }, ct2);
            }
            else
            {
                // Decline → AlternativesRequired → FailedFinal
                var rows = await opRepo.TryTransitionStatusAsync(
                    command.TenantId, command.OperationId, current.Version,
                    OperationStatus.AlternativesRequired, OperationStatus.FailedFinal,
                    reason: "alternatives_declined", ct: ct2);

                if (rows == 0)
                    throw new OperationConflictException(
                        command.OperationId, "Concurrent update — retry.");

                // Spec 11 §5.3: a pre-order op signals the saga (→ compensate items) instead of
                // emitting the customer failure notification directly.
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.FailedOutcomeType(current.IsPreorder),
                    CorrelationId = command.OperationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        tenant_id = command.TenantId,
                        reason = "alternatives_declined",
                    }),
                    OccurredAt = now,
                }, ct2);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "operation.failed_final",
                    Actor = "system",
                    SubjectRef = command.OperationId,
                    CorrelationId = command.OperationId,
                    OperationId = command.OperationId,
                    Metadata = JsonSerializer.Serialize(new { reason = "alternatives_declined" }),
                    OccurredAt = now,
                }, ct2);
            }
        }, ct);

        return Unit.Value;
    }
}
