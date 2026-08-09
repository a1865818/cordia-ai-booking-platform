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
    ModifyBooking.ModifyCapacitySettlement capacitySettlement,
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
                // Resolved BEFORE the transition, for the reason the automatic lane documents:
                // `GetBookedOperationForBookingAsync` returns the NEWEST Booked operation for the
                // booking, so once this one is Booked it resolves to itself and the seat the
                // booking is leaving is never given up.
                var previousOwner = isMutation && !string.IsNullOrWhiteSpace(opInTxn.BookingId)
                    ? await opRepo.GetBookedOperationForBookingAsync(
                        command.TenantId, opInTxn.BookingId, ct2)
                    : null;

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
                    // The booking must exist. Declaring a mutation successful against a booking row
                    // that is gone commits an operation, an outbox message and a guest notification
                    // for something with no subject.
                    var target = await bookingRepo.GetByIdAsync(command.TenantId, opInTxn.BookingId, ct2)
                        ?? throw new OperationConflictException(command.OperationId,
                            "The booking this operation was acting on no longer exists; it cannot be resolved as booked.");

                    if (opInTxn.OperationType == Domain.Generated.OperationTypeEnum.Cancel)
                    {
                        // The lifecycle transition is a HARD PRECONDITION for the whole resolution,
                        // the same rule CancelBookingCommandHandler applies — not an optional side
                        // effect guarded by an `if`.
                        //
                        // It used to be reached only when the booking was CancellationPending, and
                        // its result was ignored. Both gaps committed the same lie: outside that
                        // state the block was skipped entirely, and a zero-row result fell through,
                        // so the operation recorded Booked and the outbox announced the cancellation
                        // had succeeded while the booking stayed LIVE — a guest told their booking
                        // was cancelled, sitting in a slot the venue still has reserved.
                        //
                        // Not gated on CancellationPending: a cancel accepted through the spec-04
                        // door never parks the booking there at all, so requiring it would refuse the
                        // very operations this lane exists to recover. Already-Cancelled is settled
                        // (idempotent — another writer did it, and released the seat with it);
                        // anything else must transition here or the resolution is refused.
                        var alreadyCancelled = target.LifecycleStatus == BookingLifecycleStatus.Cancelled;

                        var cancelled = alreadyCancelled
                            ? 0
                            : await bookingRepo.TryTransitionLifecycleAsync(
                                command.TenantId, target.BookingId, target.Version,
                                BookingLifecycleStatus.Cancelled, now, ct2);

                        if (!alreadyCancelled && cancelled == 0)
                            throw new OperationConflictException(command.OperationId,
                                "The booking moved while this review was being resolved; it was not cancelled, "
                                + "so the operation cannot be recorded as booked.");

                        // The seat goes back — released against the operation that BOOKED it, not
                        // against this cancellation.
                        //
                        // A cancel never reserves capacity (ValidateOperationCommandHandler routes
                        // only non-Cancel types through ReservingCapacity), so there is no hold
                        // under `command.OperationId` and releasing against it affects zero rows —
                        // the slot would stay occupied by a booking that no longer exists, which is
                        // exactly the leak this block was added to close. CancelBookingCommandHandler
                        // resolves the owner the same way for the same reason.
                        //
                        // UNCONDITIONAL, including the already-Cancelled path — the same shape the
                        // sibling handler uses, and for the same reason: releasing a seat that is
                        // already free affects zero rows, so gating on "did this transaction do the
                        // cancelling" buys nothing and loses the case where a booking reached
                        // Cancelled with its seat still held (the earlier writer found no Booked
                        // operation to release against because the creating one had not got there
                        // yet). This resolution is the last thing that will ever look at it.
                        var origin = await opRepo.GetBookedOperationForBookingAsync(
                            command.TenantId, target.BookingId, ct2);

                        if (origin is not null)
                        {
                            await capacityReservation.ReleaseFinalizedByOperationAsync(
                                command.TenantId, origin.OperationId, ReleaseReason.ProviderReleased, ct2);
                        }
                    }
                    else if (opInTxn.OperationType is Domain.Generated.OperationTypeEnum.Modify
                                 or Domain.Generated.OperationTypeEnum.Reschedule)
                    {
                        // A booking that is CANCELLED — or on its way there — cannot be amended.
                        // The automatic lane makes the same refusal before it touches anything
                        // (`target_booking_cancelled`).
                        //
                        // A cancellation can win the booking row while this review sits waiting for
                        // a human. Without this the resolution went on to finalize the new hold,
                        // release the previous owner's seat, record the operation Booked and emit a
                        // CONFIRMED outcome — for a booking that is cancelled, or about to be.
                        //
                        // `CancellationPending` counts. It is the state `POST .../cancellations`
                        // parks while a cancellation is in flight, and declaring an amendment
                        // confirmed on top of one tells the guest their booking moved while another
                        // operation is actively removing it. Stated as an allow-list of the two
                        // states an amendment may settle from, so a lifecycle value added later
                        // fails closed rather than falling through this branch unnoticed.
                        if (target.LifecycleStatus is not (BookingLifecycleStatus.Confirmed
                                or BookingLifecycleStatus.ModificationPending))
                        {
                            throw new OperationConflictException(command.OperationId,
                                $"The booking is {target.LifecycleStatus} and cannot have an amendment "
                                + "recorded as booked.");
                        }

                        if (target.LifecycleStatus == BookingLifecycleStatus.ModificationPending)
                        {
                            // A modified booking is still confirmed — what moved lives on the revision.
                            //
                            // Version-guarded, and the result is a PRECONDITION rather than a hint: a
                            // concurrent writer moving the booking makes this affect zero rows, and
                            // proceeding then settled capacity and committed the operation as Booked
                            // while the booking stayed at ModificationPending forever — the outbox
                            // announcing success over a row still claiming a change was outstanding.
                            var cleared = await bookingRepo.TryTransitionLifecycleAsync(
                                command.TenantId, target.BookingId, target.Version,
                                BookingLifecycleStatus.Confirmed, now, ct2);

                            if (cleared == 0)
                                throw new OperationConflictException(command.OperationId,
                                    "The booking moved while this review was being resolved; "
                                    + "its pending change could not be settled.");
                        }

                        // Otherwise the booking is Confirmed — the only other state the guard above
                        // admits — and there is nothing parked to clear: a reschedule accepted
                        // through the spec-04 door never parks the booking at ModificationPending,
                        // so requiring that state here would refuse every console reschedule this
                        // lane exists to recover.

                        // The SEAT has to be settled too, and nothing else will ever do it.
                        //
                        // A modify that went ambiguous routes Finalizing→Reconciling BEFORE
                        // CommitModificationAsync runs, so its capacity was left exactly as the
                        // reserve step left it: this operation's hold on the target slot still
                        // ACTIVE, and the previous owner's hold on the old slot still FINALIZED.
                        // Resolving the review as Booked settled the operation and the booking and
                        // stopped there, which leaks in both directions at once — the old slot stays
                        // occupied by a booking that moved away, and the new slot's hold is dropped
                        // by the expiry sweeper, after which the slot reads free while a confirmed
                        // booking sits in it. Neither is recoverable later: a cancellation resolves
                        // the NEWEST booked operation, which is this one, and finds nothing
                        // finalized to release.
                        await capacitySettlement.SettleAsync(
                            command.TenantId, opInTxn, previousOwner, ct2);
                    }
                }
                else
                {
                    // A CREATE resolved as Booked has a seat to settle too.
                    //
                    // The mutation branch above was given one and this one was not, but the leak is
                    // the same shape: an operation that reached ManualReview never ran
                    // `FinalizeBookingCommandHandler`, so its hold on the slot is still ACTIVE.
                    // Declaring the operation Booked and stopping there leaves the expiry sweeper to
                    // drop that hold, after which the slot reports the seats FREE while a terminal
                    // Booked operation occupies them — and no later cancellation can give them back,
                    // because releasing resolves the newest booked operation and finds nothing
                    // finalized under it.
                    //
                    // Best-effort by design: the operator has already confirmed with the provider
                    // that the booking exists, so there is no refusal available here. A hold the
                    // sweeper already reclaimed answers `HoldNoLongerValid` and leaves the ledger
                    // where it was — the discrepancy stays with the operator already looking at this
                    // operation rather than blocking a resolution that is otherwise correct.
                    await capacityReservation.FinalizeAgainstOperationAsync(
                        command.TenantId, command.OperationId, "create", ct2);
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
                // Failed means NOTHING HAPPENED, so the pending state reverts to Confirmed: the
                // amendment was not applied and the cancellation did not land, which leaves the
                // original booking exactly as it was. A booking that went Cancelled underneath us is
                // left alone, because that outcome was decided by a writer that did know.
                //
                // Shared with the other terminal-failure routes rather than copied a third time —
                // and the shared rule matches the pending state to the OPERATION'S OWN TYPE. Two
                // mutations can be in flight against one booking, and reverting on either pending
                // state meant a failed Modify whose booking had since moved to CancellationPending
                // pushed it back to Confirmed, silently undoing an unrelated in-flight cancellation
                // (and the inverse for a failed Cancel against a ModificationPending booking).
                await BookingPendingLifecycle.RevertOnFailureAsync(
                    bookingRepo, command.TenantId, opInTxn, now, ct2);

                // Give back any seat THIS operation had already taken.
                //
                // "No capacity moves — the seat was never given up" used to be stated here as a
                // premise, and it no longer holds. `modify_capacity_unavailable` routes an
                // operation to Reconciling AFTER `FinalizeAgainstOperationAsync` has flipped its
                // hold to Finalized and moved units held→booked on the slot; only then does the
                // absorb refuse. From there the reconciler escalates to ManualReview, and resolving
                // that review as Failed left those booked units owned by an operation nobody will
                // ever look at again: `GetBookedOperationForBookingAsync` matches only `Booked`
                // operations, so no cancel and no later amendment can find a FailedFinal one's
                // hold. The slot under-reported its availability permanently.
                //
                // Safe for every other route into this branch, and that is why it is not narrowed
                // to one reason: releasing is scoped to holds under THIS operation id, and the
                // operations that reach here by any other path hold nothing Finalized — the
                // ambiguous-provider and `hold_no_longer_valid` routes never finalized, and
                // `target_cancelled_in_flight` already released what it did. Those calls affect
                // zero rows.
                //
                // Crucially this cannot strip the booking of its real seat: the only route that
                // leaves a Finalized hold here is the one where the absorb FAILED, so the previous
                // owner still holds the booking's capacity. `Compensated`, because the operation is
                // being declared failed and its units are going back to the venue.
                await capacityReservation.ReleaseFinalizedByOperationAsync(
                    command.TenantId, command.OperationId, ReleaseReason.Compensated, ct2);

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
