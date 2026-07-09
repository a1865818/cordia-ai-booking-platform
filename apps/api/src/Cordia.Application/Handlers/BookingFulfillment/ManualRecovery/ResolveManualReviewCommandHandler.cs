using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;

/// <summary>
/// Handles <see cref="ResolveManualReviewCommand"/> (spec 06 §11, §17.4, Inv-15).
///
/// Protocol (all work in one transaction):
///   1. Guard: op exists + ManualReview status.
///   2. Booked: require non-empty ProviderRef; ManualReview → Booked (bookingId=ProviderRef);
///      notification.booking_confirmed.v1 outbox (Inv-8).
///      FailedFinal: ManualReview → FailedFinal; notification.booking_failed.v1 outbox.
///   3. rows==0 → OperationConflictException (409).
///   4. Close the open work item (open → resolved).
///   5. Audit operation.manual_resolved with ReplayCommandId (Inv-15).
/// </summary>
public sealed class ResolveManualReviewCommandHandler(
    IBookingOperationRepository opRepo,
    IOperationWorkItemRepository workItemRepo,
    IOutboxRepository outboxRepo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<ResolveManualReviewCommand, Unit>
{
    public async Task<Unit> Handle(ResolveManualReviewCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            throw new OperationConflictException(command.OperationId,
                "Operation not found for resolve.");

        if (op.Status != OperationStatus.ManualReview)
            throw new OperationConflictException(command.OperationId,
                "Operation is not in ManualReview; cannot resolve.");

        if (command.Resolution == ManualReviewResolution.Booked
            && string.IsNullOrWhiteSpace(command.ProviderRef))
            throw new OperationConflictException(command.OperationId,
                "ProviderRef is required when resolving as Booked.");

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Re-read inside transaction for a consistent version snapshot.
            var opInTxn = await opRepo.GetAsync(command.OperationId, ct2);
            if (opInTxn is null || opInTxn.TenantId != command.TenantId)
                throw new OperationConflictException(command.OperationId,
                    "Operation not found for resolve.");

            if (opInTxn.Status != OperationStatus.ManualReview)
                throw new OperationConflictException(command.OperationId,
                    "Operation is not in ManualReview; cannot resolve.");

            var now = DateTimeOffset.UtcNow;
            int rows;

            if (command.Resolution == ManualReviewResolution.Booked)
            {
                // ManualReview → Booked (§11 legal edge: "verified external result").
                rows = await opRepo.TryTransitionStatusAsync(
                    command.TenantId,
                    command.OperationId,
                    opInTxn.Version,
                    OperationStatus.ManualReview,
                    OperationStatus.Booked,
                    reason: "manual_resolved_booked",
                    bookingId: command.ProviderRef,
                    ct: ct2);

                if (rows == 0)
                    throw new OperationConflictException(command.OperationId,
                        "Concurrent modification — could not transition ManualReview → Booked.");

                // Inv-8: outcome in the same transaction. Spec 11 §5.3: a pre-order op signals the
                // saga instead of notifying the customer directly.
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.ConfirmedOutcomeType(opInTxn.IsPreorder),
                    CorrelationId = command.OperationId,
                    CausationId = command.ReplayCommandId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        booking_id = command.ProviderRef,
                        tenant_id = command.TenantId,
                    }),
                    OccurredAt = now,
                }, ct2);
            }
            else
            {
                // ManualReview → FailedFinal (§11 legal edge: "resolved").
                rows = await opRepo.TryTransitionStatusAsync(
                    command.TenantId,
                    command.OperationId,
                    opInTxn.Version,
                    OperationStatus.ManualReview,
                    OperationStatus.FailedFinal,
                    reason: "manual_resolved_failed",
                    ct: ct2);

                if (rows == 0)
                    throw new OperationConflictException(command.OperationId,
                        "Concurrent modification — could not transition ManualReview → FailedFinal.");

                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.FailedOutcomeType(opInTxn.IsPreorder),
                    CorrelationId = command.OperationId,
                    CausationId = command.ReplayCommandId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        tenant_id = command.TenantId,
                    }),
                    OccurredAt = now,
                }, ct2);
            }

            // Close the open work item (if any) — open → resolved.
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

            // Audit with the distinct ReplayCommandId (Inv-15).
            // Spec 14 §24.5 — operator action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: "operator",
                ActorId: command.Actor,
                Action: "operation.manual_resolved",
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
                    resolution = command.Resolution.ToString().ToLowerInvariant(),
                }), ct2);
        }, ct);

        return Unit.Value;
    }
}
