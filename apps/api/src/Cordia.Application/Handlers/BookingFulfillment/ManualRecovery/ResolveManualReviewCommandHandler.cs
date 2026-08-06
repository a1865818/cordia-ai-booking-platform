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
    IBookingRepository bookingRepo,
    ICapacityReservation capacityReservation,
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

            // Is this a MUTATION of an existing booking rather than a create?
            //
            // It changes two things, and getting either wrong leaves the booking inconsistent in a
            // way no later path repairs:
            //
            //   * `bookingId` must NOT be overwritten. On a create the provider reference IS the
            //     booking id; on a cancel or modify it is the provider's own reference for the
            //     amendment, and writing it over `op.BookingId` severs the operation from the
            //     booking it was acting on — the link every downstream reader follows.
            //   * the booking's own pending lifecycle must be settled. A cancel parks the booking
            //     at CancellationPending and a modify at ModificationPending; the generic resolver
            //     touches neither, so a resolved mutation left every reader of the booking row
            //     told a change was still outstanding, permanently.
            var isMutation = opInTxn.OperationType is Domain.Generated.OperationTypeEnum.Cancel
                or Domain.Generated.OperationTypeEnum.Modify
                or Domain.Generated.OperationTypeEnum.Reschedule;

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
                    // Only a CREATE mints the booking id. See the note above.
                    bookingId: isMutation ? null : command.ProviderRef,
                    ct: ct2);

                if (rows == 0)
                    throw new OperationConflictException(command.OperationId,
                        "Concurrent modification — could not transition ManualReview → Booked.");

                // Settle the booking the mutation was acting on, so the row stops claiming a
                // change is still in flight. Version-guarded and status-guarded: a booking that
                // moved underneath us keeps whatever the winner decided.
                if (isMutation && !string.IsNullOrWhiteSpace(opInTxn.BookingId))
                {
                    var target = await bookingRepo.GetByIdAsync(command.TenantId, opInTxn.BookingId, ct2);

                    if (opInTxn.OperationType == Domain.Generated.OperationTypeEnum.Cancel
                        && target is { LifecycleStatus: BookingLifecycleStatus.CancellationPending })
                    {
                        await bookingRepo.TryTransitionLifecycleAsync(
                            command.TenantId, target.BookingId, target.Version,
                            BookingLifecycleStatus.Cancelled, now, ct2);

                        // The seat goes back — released against the operation that BOOKED it, not
                        // against this cancellation.
                        //
                        // A cancel never reserves capacity (ValidateOperationCommandHandler routes
                        // only non-Cancel types through ReservingCapacity), so there is no hold
                        // under `command.OperationId` and releasing against it affects zero rows —
                        // the slot would stay occupied by a booking that no longer exists, which is
                        // exactly the leak this block was added to close. CancelBookingCommandHandler
                        // resolves the owner the same way for the same reason.
                        var origin = await opRepo.GetBookedOperationForBookingAsync(
                            command.TenantId, target.BookingId, ct2);

                        if (origin is not null)
                        {
                            await capacityReservation.ReleaseFinalizedByOperationAsync(
                                command.TenantId, origin.OperationId, ReleaseReason.ProviderReleased, ct2);
                        }
                    }
                    else if (opInTxn.OperationType is Domain.Generated.OperationTypeEnum.Modify
                                 or Domain.Generated.OperationTypeEnum.Reschedule
                             && target is { LifecycleStatus: BookingLifecycleStatus.ModificationPending })
                    {
                        // A modified booking is still confirmed — what moved lives on the revision.
                        await bookingRepo.TryTransitionLifecycleAsync(
                            command.TenantId, target.BookingId, target.Version,
                            BookingLifecycleStatus.Confirmed, now, ct2);
                    }
                }

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
                        // The BOOKING id, which on a mutation is not the provider reference.
                        //
                        // Same rule the transition above applies: for a cancel or modify the
                        // operator supplies the provider's reference for the AMENDMENT, so shipping
                        // it as `booking_id` sent every consumer — notification templating, the
                        // pre-order saga's combined outcome — an id that matches no booking.
                        booking_id = isMutation ? opInTxn.BookingId : command.ProviderRef,
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

                // A FAILED mutation must release the booking's pending state too, and this branch
                // is the one that was missed.
                //
                // `POST .../modifications` parks the booking at ModificationPending and
                // `.../cancellations` at CancellationPending; only the automatic success paths ever
                // cleared them. A modify or cancel that went ambiguous routes Finalizing→Reconciling,
                // the reconciler escalates it straight to ManualReview, and if the operator then
                // resolves it as failed the operation reads `failed_final` while the booking row
                // still tells every reader a change is outstanding — permanently, because nothing
                // downstream of here ever looks at it again.
                //
                // Failed means NOTHING HAPPENED, so both revert to Confirmed: the amendment was not
                // applied and the cancellation did not land, which leaves the original booking
                // exactly as it was. No capacity moves for the same reason — the seat was never
                // given up. A booking that went Cancelled underneath us is left alone (the status
                // guard), because that outcome was decided by a writer that did know.
                if (isMutation && !string.IsNullOrWhiteSpace(opInTxn.BookingId))
                {
                    var target = await bookingRepo.GetByIdAsync(command.TenantId, opInTxn.BookingId, ct2);

                    if (target is
                        {
                            LifecycleStatus: BookingLifecycleStatus.ModificationPending
                                    or BookingLifecycleStatus.CancellationPending
                        })
                    {
                        await bookingRepo.TryTransitionLifecycleAsync(
                            command.TenantId, target.BookingId, target.Version,
                            BookingLifecycleStatus.Confirmed, now, ct2);
                    }
                }

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
                ActorType: command.ActorRole, // GD-1: owner may perform operator actions; audit the ASSERTED role
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
