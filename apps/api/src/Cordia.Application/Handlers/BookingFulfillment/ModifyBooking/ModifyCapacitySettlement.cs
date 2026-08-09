using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.BookingFulfillment.ModifyBooking;

/// <summary>
/// Makes the capacity ledger reflect a modify/reschedule that has been declared BOOKED.
///
/// <para>Used by the manual-recovery lane. A modify that went ambiguous routes
/// <c>Finalizing → Reconciling</c> BEFORE <c>ModifyBookingCommandHandler</c> ever settles anything,
/// so its capacity is left exactly as the reserve step left it: this operation's hold on the target
/// slot still ACTIVE, and the previous owner's hold on the old slot still FINALIZED. An operator
/// who confirms with the venue that the move did happen and resolves the review as Booked settles
/// the operation and the booking — and, without this, nothing at all settles the seat.</para>
///
/// <para>That leaks in both directions at once. The old slot stays occupied by a booking that moved
/// away; the new slot's hold is dropped by the expiry sweeper, after which the slot reads free
/// while a confirmed booking sits in it. Neither self-heals, because a later cancellation resolves
/// the NEWEST booked operation — this one — and finds no finalized hold to release.</para>
///
/// <para><b>Deliberately separate from the automatic lane.</b> <c>CommitModificationAsync</c> runs
/// this decision while the operation is still <c>Finalizing</c>, so it can refuse: an expired hold
/// or a slot that cannot take the new size routes to Reconciling instead of booking. Here the
/// operator has already asserted the provider applied the amendment and the operation is Booked, so
/// there is no refusal available — the ledger is brought into line with a decision already made.
/// The two share the same primitives and the same same-slot test, not the same control flow.</para>
/// </summary>
public sealed class ModifyCapacitySettlement(
    IBookingIntentRepository intentRepo,
    ICapacityHoldRepository holdRepo,
    ICapacityLockOrder lockOrder,
    ICapacityReservation capacityReservation)
{
    private const string CreateStep = "create";

    /// <summary>
    /// Settles the seat for <paramref name="op"/>.
    ///
    /// <para><paramref name="previousOwner"/> MUST have been resolved BEFORE the operation was
    /// transitioned to Booked. <c>GetBookedOperationForBookingAsync</c> returns the newest Booked
    /// operation for the booking, so once this one is Booked it resolves to ITSELF, the identity
    /// guard skips, and the old slot leaks forever — the same trap the automatic lane documents.</para>
    /// </summary>
    public async Task SettleAsync(
        string tenantId,
        BookingOperation op,
        BookingOperation? previousOwner,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(op.BookingId))
            return;

        var revision = await intentRepo.GetRevisionAsync(tenantId, op.IntentId, op.IntentRevision, ct);
        var (requestedCapacity, capacityIsExplicit, targetSlotId) =
            CapacitySlotKey.DeriveFromCanonicalPayload(revision?.CanonicalPayload);

        var previousHold = previousOwner is not null && previousOwner.OperationId != op.OperationId
            ? await holdRepo.GetLatestByOperationAndStepAsync(
                tenantId, previousOwner.OperationId, CreateStep, ct)
            : null;

        // Is the booking actually MOVING? A same-slot amendment resizes the seat in place; a
        // genuine reschedule hands it from one slot to another. Same question, same two facts and
        // same answer as the automatic lane.
        var sameSlot = previousHold is { Status: HoldStatus.Finalized }
                       && previousHold.SlotId is not null
                       && previousHold.SlotId == targetSlotId;

        // And where did it move TO? A null key is "not first-party tracked", not "a different
        // slot" — a `SubmitModificationInput` canonical payload carries no time or resource
        // dimension at all, so it derives null for every modification. Releasing the previous
        // owner's finalized hold on that basis puts a confirmed booking's seats back on sale
        // while the guest still occupies them. Withheld only where there is a real first-party seat
        // to lose: a previous owner holding no slot has nothing to give up, so that path is
        // untouched. The automatic lane makes the same distinction for the same reason; these two
        // must not drift.
        var movedToKnownSlot = !sameSlot
                               && (targetSlotId is not null || previousHold?.SlotId is null);

        // An amendment that does not restate the party size does not change it — `AbsorbSameSlotAsync`
        // reads this as the authoritative total, so the fallback of 1 would resize the booking down.
        // Same rule as the automatic lane and as the reserve step that sized the delta.
        var targetCapacity = capacityIsExplicit || previousHold is null
            ? requestedCapacity
            : previousHold.ReservedCapacity;

        // Acquire every capacity row this settlement will touch, in canonical order — the same
        // acquisition the automatic lane makes, for the same two cycles (hold↔slot between modify
        // and cancel; slot↔slot between two reschedules moving in opposite directions between the
        // same pair of slots). See `ICapacityLockOrder` for the rule.
        //
        // This lane is reached from ResolveManualReviewCommandHandler, so it carries whatever
        // inversion the automatic lane carries. Fixing only the automatic lane has already left the
        // manual-recovery path deadlocking once; the acquisition is deliberately identical here so
        // the two cannot drift again.
        //
        // Unconditional, not gated on `sameSlot`: the cross-slot branch is precisely the one that
        // touches two different slot rows, so it is the branch that needs the slot ordering most.
        var ownHold = await holdRepo.GetLatestByOperationAndStepAsync(
            tenantId, op.OperationId, CreateStep, ct);

        await lockOrder.AcquireAsync(
            tenantId,
            holdIds: [previousHold?.HoldId, ownHold?.HoldId],
            slotIds: [previousHold?.SlotId, targetSlotId],
            ct);

        // Promote this operation's own hold. Idempotent, and NotTracked for a venue that declared
        // no slots — capacity is unenforced there by decision, not by accident.
        var finalizeOutcome = await capacityReservation.FinalizeAgainstOperationAsync(
            tenantId, op.OperationId, CreateStep, ct);

        // The new slot's hold is GONE, so there is no seat to hand the booking to.
        //
        // This lane is reached only through ManualReview, which waits for a human — routinely far
        // longer than `HoldTtl` — so by the time an operator resolves the review, the expiry
        // sweeper has very often already released this operation's hold on the target slot. That
        // makes this the common case here, not a rare race.
        //
        // Releasing the OLD slot anyway would leave a confirmed booking counted against neither
        // slot: the seat the guest actually occupies goes back on sale, and the slot they were
        // moved to has nothing recorded. The automatic lane refuses for exactly this reason
        // ("the guest must not end up holding neither seat"); this lane discarded the outcome and
        // released regardless.
        //
        // Keeping the original seat is the safe end state: the booking still occupies something
        // real, and the discrepancy is left for the operator already looking at this booking —
        // the same disposition the same-slot branch below takes when it cannot absorb.
        if (finalizeOutcome == FinalizeOutcome.HoldNoLongerValid)
            return;

        // `NotTracked` is not a successful finalization — it means NO first-party hold was ever
        // created for this operation, so there is no destination seat at all.
        //
        // The same reasoning as `HoldNoLongerValid` immediately above, and it was missed because
        // both non-Finalized outcomes look alike from the outside. A reschedule whose old slot is
        // tracked and finalized but whose new slot never got a hold still derives a non-null
        // `targetSlotId`, so `movedToKnownSlot` was true and the previous owner's FINALIZED hold
        // was released: the confirmed booking ended up with no capacity on either slot and its
        // secured seat went back on sale.
        //
        // Only the RELEASE is gated. The absorb below is a same-slot resize that does not depend on
        // a new hold, and an operation whose previous owner holds no first-party seat has nothing to
        // lose — that path stays exactly as it was, so an untracked venue is unaffected.
        var destinationSecuredOrNothingToLose =
            finalizeOutcome == FinalizeOutcome.Finalized
            || previousHold is not { Status: HoldStatus.Finalized, SlotId: not null };

        if (sameSlot)
        {
            // Exactly one finalized hold survives, sized to the whole booking. A refusal here
            // cannot be routed anywhere — the operator has already declared the move done — so the
            // booking keeps the seat it had and the discrepancy is left for the operator who is
            // already looking at this booking, rather than silently resized.
            await capacityReservation.AbsorbSameSlotAsync(
                tenantId, previousOwner!.OperationId, op.OperationId, CreateStep,
                previousHold!.SlotId!, targetCapacity, ct);
        }
        else if (movedToKnownSlot
                 && destinationSecuredOrNothingToLose
                 && previousOwner is not null
                 && previousOwner.OperationId != op.OperationId)
        {
            // `Superseded`, not `ProviderReleased`: the seat is handed from the old slot to the new
            // one by this operation, not given back to the venue. Reached only when the new seat is
            // actually secured — the guards above are what make "handed" true rather than "dropped".
            await capacityReservation.ReleaseFinalizedByOperationAsync(
                tenantId, previousOwner.OperationId, ReleaseReason.Superseded, ct);
        }
    }
}
