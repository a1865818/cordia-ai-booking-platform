using System.Text.Json;
using Cordia.Application.Common.Audit;
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
    IOperationWorkItemRepository workItemRepo,
    IOutboxRepository outboxRepo,
    IAuditWriter audit,
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

            // A mutation cannot be resumed into reconciliation, and offering it would be a loop,
            // not a recovery.
            //
            // Resume means ManualReview → Reconciling, and `ReconcileOperationCommandHandler`
            // escalates Cancel/Modify/Reschedule straight back to ManualReview on sight — because
            // its lookup is create-shaped and cannot establish whether a cancellation landed or an
            // amendment applied. So the operator would press Resume, watch the row leave
            // ManualReview, and watch it come straight back, forever, with nothing changed.
            //
            // Refused with a message that names the door that DOES work: Resolve, where a human
            // supplies the outcome the platform cannot determine for itself.
            if (opInTxn.OperationType is Domain.Generated.OperationTypeEnum.Cancel
                or Domain.Generated.OperationTypeEnum.Modify
                or Domain.Generated.OperationTypeEnum.Reschedule)
            {
                throw new OperationConflictException(
                    command.OperationId,
                    "A cancel, modify or reschedule cannot be resumed: reconciliation cannot "
                    + "establish whether the provider applied it, so it would return to manual "
                    + "review unchanged. Resolve it instead, supplying the outcome you verified.");
            }

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

            // C10: close the open work item (if any) — open → resolved, mirroring ResolveManualReviewCommandHandler.
            var workItem = await workItemRepo.GetOpenByOperationAsync(
                command.TenantId, command.OperationId, ct2);
            if (workItem is not null)
            {
                await workItemRepo.TryUpdateStateAsync(
                    command.TenantId,
                    workItem.WorkItemId,
                    workItem.Version,
                    expectedState: "open",
                    nextState: "resolved",
                    assignee: command.Actor,
                    ct: ct2);
            }

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
            // Spec 14 §24.5 — operator action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: command.ActorRole, // GD-1: owner may perform operator actions; audit the ASSERTED role
                ActorId: command.Actor,
                Action: "operation.replay",
                TargetType: "booking_operation",
                TargetId: command.OperationId,
                Decision: "allow",
                DataClasses: ["booking"],
                ReasonCode: command.ReasonCode,
                CorrelationId: command.OperationId,
                OperationId: command.OperationId,
                OccurredAt: now,
                Extra: new
                {
                    replay_command_id = command.ReplayCommandId,
                    actor = command.Actor,
                    kind = "resume",
                }), ct2);
        }, ct);

        return Unit.Value;
    }
}
