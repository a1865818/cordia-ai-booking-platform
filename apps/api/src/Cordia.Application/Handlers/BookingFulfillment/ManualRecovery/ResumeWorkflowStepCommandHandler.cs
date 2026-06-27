using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;

/// <summary>
/// Handles <see cref="ResumeWorkflowStepCommand"/> — the authorized controlled-retry exit from
/// ManualReview (spec 06 Inv-15, §17.4, FM#15).
///
/// Protocol:
///   1. Load op; guard tenant + status (ManualReview only).
///   2. In one transaction: version-guarded ManualReview → Reconciling; emit
///      <c>booking.command.requested.v1</c> continuation outbox row so the driver re-drives
///      from the current durable state; audit <c>operation.replay</c> with ReplayCommandId.
///
/// Inv-15: replay reuses the existing step's ProviderIdempotencyKey (stored on the
/// workflow_step row — the driver's ReconcileOperationCommand reads it). AttemptNumber is
/// NEVER reset — GetLatestForStepAsync().AttemptNumber + 1 naturally continues the sequence.
/// </summary>
public sealed class ResumeWorkflowStepCommandHandler(
    IBookingOperationRepository opRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ResumeWorkflowStepCommand, Unit>
{
    public async Task<Unit> Handle(ResumeWorkflowStepCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        // Safety net: controller does a 404 existence check first; this is a concurrency guard.
        if (op is null || op.TenantId != command.TenantId)
            throw new OperationConflictException(command.OperationId,
                "Operation not found for resume.");

        if (op.Status != OperationStatus.ManualReview)
            throw new OperationConflictException(command.OperationId,
                "Operation is not in ManualReview; cannot resume.");

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Re-read inside transaction for a consistent version snapshot.
            var opInTxn = await opRepo.GetAsync(command.OperationId, ct2);
            if (opInTxn is null || opInTxn.TenantId != command.TenantId)
                throw new OperationConflictException(command.OperationId,
                    "Operation not found for resume.");

            if (opInTxn.Status != OperationStatus.ManualReview)
                throw new OperationConflictException(command.OperationId,
                    "Operation is not in ManualReview; cannot resume.");

            // Version-guarded ManualReview → Reconciling (§11 "controlled retry" edge).
            var rows = await opRepo.TryTransitionStatusAsync(
                command.TenantId,
                command.OperationId,
                opInTxn.Version,
                OperationStatus.ManualReview,
                OperationStatus.Reconciling,
                reason: "manual_resume",
                ct: ct2);

            if (rows == 0)
                throw new OperationConflictException(command.OperationId,
                    "Concurrent modification — could not transition ManualReview → Reconciling.");

            var now = DateTimeOffset.UtcNow;

            // Emit booking.command.requested.v1 continuation so the driver re-drives from the
            // current durable state. The driver routes Reconciling → ReconcileOperationCommand
            // which reuses the SAME ProviderIdempotencyKey (Inv-7/Inv-15).
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = $"rpl_{Guid.NewGuid():N}",
                OperationId = command.OperationId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = command.OperationId,
                CausationId = command.ReplayCommandId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = command.OperationId,
                    tenant_id = command.TenantId,
                }),
                OccurredAt = now,
            }, ct2);

            // Audit with the distinct ReplayCommandId (Inv-15).
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.replay",
                Actor = command.Actor,
                SubjectRef = command.OperationId,
                CorrelationId = command.OperationId,
                OperationId = command.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    replay_command_id = command.ReplayCommandId,
                    actor = command.Actor,
                    kind = "resume",
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }
}
