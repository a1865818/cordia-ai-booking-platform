using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ModifyBooking;

/// <summary>Fulfils a modify/reschedule operation at <c>Finalizing</c>.</summary>
public sealed record ModifyBookingCommand(string TenantId, string OperationId)
    : IRequest<StepOutcome>;

/// <summary>
/// Fulfils a modification or reschedule (spec 06 §11, spec 08 §9.4.8).
///
/// <para>Before this existed the fulfilment engine could only create and cancel. A modify
/// reaching <c>Finalizing</c> was refused with a <c>PoisonMessageException</c> — deliberately,
/// because <c>FinalizeBookingCommandHandler</c> calls <c>CreateBookingAsync</c> unconditionally,
/// so letting one through would have put a SECOND booking into the venue's system while the
/// original stayed pending, and reported <c>booked</c> for it.</para>
///
/// <para><b>One provider call, not cancel-then-create.</b> §9.4.8 requires a modification to
/// preserve the original reservation until a replacement is secured. Cancelling first surrenders
/// the seat, so if the new time turns out to be unavailable the guest is left with nothing —
/// worse than the modification simply failing. <c>ModifyBookingAsync</c> either moves the booking
/// or leaves it exactly as it was.</para>
///
/// Protocol (mirrors <c>CancelBookingCommandHandler</c>, the proven staged-call shape):
///   1. Load the operation; guard tenant, status == Finalizing, type == Modify | Reschedule.
///   2. Load the target booking from <c>op.BookingId</c>.
///   3. Create-or-load the "modify" workflow step (idempotent, Inv-5).
///   4. Call <c>adapter.ModifyBookingAsync</c> <b>outside</b> the transaction (§12.6).
///   5a. Created / AlreadyExists / NotFound — one transaction: step → Succeeded; operation
///       Finalizing → Booked (reason "modified"); outbox <c>cancel_or_modify_confirmed</c>; audit.
///   5b. Ambiguous / Transient / PermanentError / AuthError ⇒ Finalizing → Reconciling (Inv-7).
///       Never a blind retry — we do not know whether the provider applied the amendment.
///
/// <para><b>The booking's lifecycle does NOT change.</b> A modified booking is still confirmed;
/// only its details moved, and those live on the new intent revision, which the read projection
/// already joins. Flipping the lifecycle would tell every reader the booking had been cancelled
/// and recreated, which is precisely what did not happen.</para>
///
/// <para><b>Inv-8:</b> the operation transition and the notification outbox row commit in the
/// same transaction, so a guest can never be told "moved" before it is true.</para>
/// </summary>
public sealed class ModifyBookingCommandHandler(
    IBookingOperationRepository opRepo,
    IBookingRepository bookingRepo,
    IBookingIntentRepository intentRepo,
    IWorkflowStepRepository stepRepo,
    IBookingProviderAdapter adapter,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow,
    ICapacityReservation capacityReservation,
    ICapacityHoldRepository holdRepo,
    ICapacityLockOrder lockOrder,
    Metrics metrics)
    : IRequestHandler<ModifyBookingCommand, StepOutcome>
{
    private const string ModifyStepType = "modify";
    private const string ProviderAccount = "default";

    /// <summary>
    /// Shared with cancel by design: the ratified event is <c>cancel_or_modify_confirmed</c>, and
    /// its template covers both outcomes. Minting a second message type would need a registry
    /// entry that does not exist, and an unregistered type dead-letters.
    /// </summary>
    public const string ModifyConfirmedEventType = "notification.cancel_confirmed.v1";

    public async Task<StepOutcome> Handle(ModifyBookingCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.Finalizing)
            return StepOutcome.Continue;

        if (op.OperationType is not (Domain.Generated.OperationTypeEnum.Modify
            or Domain.Generated.OperationTypeEnum.Reschedule))
        {
            return StepOutcome.Continue;
        }

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        var revision = await intentRepo.GetRevisionAsync(tenantId, op.IntentId, op.IntentRevision, ct)
            ?? throw new PoisonMessageException(
                $"Modify operation '{operationId}' has no revision {op.IntentRevision} to apply.");

        // The operation row is the primary source, but acceptance only began carrying the target
        // recently — fall back to the canonical payload, which is where a mutation's target has
        // always lived, so operations accepted before that still fulfil.
        var targetBookingId = string.IsNullOrWhiteSpace(op.BookingId)
            ? ReadTargetBookingId(revision.CanonicalPayload)
            : op.BookingId;

        // A modification with no target is a defect, not a transient failure: retrying can never
        // discover one. Poison it so it is quarantined and surfaced (Inv-14) rather than looping.
        if (string.IsNullOrWhiteSpace(targetBookingId))
            throw new PoisonMessageException(
                $"Modify operation '{operationId}' carries no target booking id.");

        var booking = await bookingRepo.GetByIdAsync(tenantId, targetBookingId, ct)
            ?? throw new PoisonMessageException(
                $"Modify operation '{operationId}' targets booking '{targetBookingId}', which does not exist.");

        // Write the resolved target BACK onto the operation row when it came from the fallback.
        //
        // Everything downstream reads `op.BookingId`, not this local — and the recovery paths are
        // where that bites. An ambiguous outcome routes to Reconciling, the reconciler escalates to
        // ManualReview, and ResolveManualReviewCommandHandler settles the target booking only when
        // `opInTxn.BookingId` is populated. For an operation relying on the payload fallback it
        // never is, so the resolver skipped the settlement and its capacity cleanup and left the
        // booking at ModificationPending permanently — the operator resolves the review and the
        // booking still tells every reader a change is outstanding.
        //
        // Conditional on it still being null, so this can never overwrite a target another writer
        // established, and idempotent across redeliveries.
        if (string.IsNullOrWhiteSpace(op.BookingId))
            await opRepo.TrySetBookingIdAsync(tenantId, operationId, targetBookingId, ct);

        // A cancelled booking cannot be moved. This is a business outcome, not a failure to
        // retry: the guest must make a new booking.
        //
        // Routed to CompensationRequired, NOT Rejected. §11 gives Finalizing exactly three
        // successors — Booked, Reconciling, CompensationRequired — and `TryTransitionStatusAsync`
        // guards status+version without consulting the edge set, so the illegal Finalizing→Rejected
        // edge simply succeeded and left OperationStateMachine no longer describing what the code
        // does. It also stranded capacity: this operation is holding an Active hold on the NEW slot
        // (every non-Cancel type reserves before Finalizing) and a terminal Rejected has no path
        // that gives it back, so the seat sat reserved until the expiry sweeper noticed.
        // CompensationRequired routes to CompensateOperationCommandHandler, which finds no
        // provider effect to undo (nothing was called yet), releases the hold, and terminalizes as
        // FailedFinal with the guest told the change did not happen — the outcome this branch
        // always intended.
        if (booking.LifecycleStatus == BookingLifecycleStatus.Cancelled)
        {
            return await RouteToTerminalAsync(
                tenantId, operationId, OperationStatus.CompensationRequired, "target_booking_cancelled", ct);
        }

        var step = await CreateOrLoadStepAsync(tenantId, operationId, ct);
        if (step is null)
            return StepOutcome.Continue;

        // ─────────────────────────────────────────────────────────────
        // External call — OUTSIDE the transaction (§12.6).
        // ─────────────────────────────────────────────────────────────
        var providerRef = booking.ProviderReference;
        var reason = "modified";

        if (!string.IsNullOrWhiteSpace(providerRef))
        {
            var result = await adapter.ModifyBookingAsync(
                ProviderAccount,
                providerRef,
                new ProviderBookingRequest(
                    tenantId,
                    operationId,
                    ProviderAccount,
                    // The operation id IS the stable key: each modification is its own operation,
                    // and a redelivery must present the same key so the provider can dedupe.
                    operationId,
                    revision.PayloadHash,
                    StripAuthorizationMaterial(revision.CanonicalPayload)),
                ct);

            // Ambiguous outcomes must reconcile before anything else happens (Inv-7): we do not
            // know whether the provider applied the amendment, so we must not report that it did.
            if (result.Kind is not (ProviderResultKind.Created or ProviderResultKind.AlreadyExists))
            {
                // NotFound is included here, and that is a change of stance.
                //
                // For a CREATE, NotFound means nothing was made and there is nothing to undo. For a
                // MODIFY it means the provider has no record of a booking this platform holds a
                // reference to — so the amendment definitively did NOT happen, and the venue is
                // still working from the original reservation. Settling that as `Booked` finalized
                // the new slot's capacity, released the old one, cleared ModificationPending and
                // told the guest their booking had moved: four statements about a booking the
                // venue says it does not have.
                //
                // It is also not a failure to retry — the booking will not reappear — so it takes
                // the same route as an ambiguous outcome: Reconciling, which escalates every
                // modify/reschedule to ManualReview for a human to reconcile the two systems.
                return await RouteToReconcilingAsync(tenantId, operationId, result.Kind, ct);
            }
        }
        else
        {
            // Never reached a provider in the first place (first-party only).
            reason = "modified_no_provider_effect";
        }

        return await CommitModificationAsync(tenantId, operationId, booking, revision, step, reason, ct);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The mutation target from the canonical payload — where it has always lived, and the only
    /// source for operations accepted before the operation row began carrying it.
    /// </summary>
    private static string? ReadTargetBookingId(string? canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(canonicalPayload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("target_booking_id", out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes AUTHORIZATION material from the payload handed to the booking provider.
    ///
    /// <para>The canonical payload of an operation accepted through
    /// <c>POST /api/Bookings/{id}/modifications</c> is a serialized <c>SubmitModificationInput</c>,
    /// which carries <c>grant_id</c> — and <c>confirmation.identity</c> holds a
    /// <c>grant_</c>-prefixed id on the agent lane. Those are the spec-08 credentials that
    /// authorize reading and mutating a booking (<c>X-Grant-Id</c>), so shipping them verbatim
    /// handed a live verification grant to a third party on every modify, where it lands in that
    /// provider's request logs. The provider has no use for either: it is being asked to amend a
    /// reservation, not to prove the caller was allowed to ask.</para>
    ///
    /// <para>Everything else is passed through untouched, and a payload that is not a JSON object
    /// (or carries neither field) is returned as-is — this must never be able to fail a modify.</para>
    /// </summary>
    private static string? StripAuthorizationMaterial(string? canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
            return canonicalPayload;

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(canonicalPayload)
                is not System.Text.Json.Nodes.JsonObject root)
            {
                return canonicalPayload;
            }

            var removed = root.Remove("grant_id");

            if (root["confirmation"] is System.Text.Json.Nodes.JsonObject confirmation
                && confirmation["identity"]?.GetValue<string>() is { } identity
                && identity.StartsWith("grant_", StringComparison.Ordinal))
            {
                confirmation.Remove("identity");
                removed = true;
            }

            return removed ? root.ToJsonString() : canonicalPayload;
        }
        catch (JsonException)
        {
            return canonicalPayload;
        }
        catch (InvalidOperationException)
        {
            // `GetValue<string>()` on a non-string node. Same disposition: a payload we cannot
            // read is passed through rather than failing the modify.
            return canonicalPayload;
        }
    }

    /// <summary>Create-or-load the "modify" workflow step so a redelivery reuses it (Inv-5).</summary>
    private async Task<WorkflowStep?> CreateOrLoadStepAsync(
        string tenantId, string operationId, CancellationToken ct)
    {
        WorkflowStep? step = null;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var existing = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, ModifyStepType, ct2);
            if (existing is not null)
            {
                step = existing;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            step = new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                OperationId = operationId,
                StepType = ModifyStepType,
                Status = WorkflowStepStatus.InProgress,
                ProviderIdempotencyKey = operationId,
                AttemptCount = 1,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await stepRepo.AddAsync(step, ct2);
        }, ct);

        return step;
    }

    /// <summary>
    /// One transaction: settle the step, the operation, the outbox and the audit together, so no
    /// observer can see a partial modification (Inv-4, Inv-8).
    /// </summary>
    private async Task<StepOutcome> CommitModificationAsync(
        string tenantId,
        string operationId,
        Booking booking,
        BookingIntentRevision revision,
        WorkflowStep step,
        string reason,
        CancellationToken ct)
    {
        var committed = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var now = DateTimeOffset.UtcNow;

            if (step.Status == WorkflowStepStatus.InProgress)
            {
                await stepRepo.TryTransitionStatusAsync(
                    tenantId, step.WorkflowStepId, step.Version,
                    WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
            }

            // Re-read inside the transaction and version-guard: a concurrent delivery may have
            // already settled this operation.
            var freshOp = await opRepo.GetAsync(operationId, ct2);
            if (freshOp is null || freshOp.Status != OperationStatus.Finalizing)
                return;

            // ── Capacity is decided BEFORE anything is settled (spec 07 §6.1/§6.2). ──
            //
            // A reschedule reserves capacity like any other operation: ValidateOperationCommandHandler
            // routes every non-Cancel type through ReservingCapacity, so this operation already holds
            // an Active hold on the NEW slot. `FinalizeAgainstOperationAsync` is the SINGLE decision
            // point for that hold — the same role it plays in FinalizeBookingCommandHandler — and its
            // answer has to be known before the operation is called Booked, before the old slot is
            // given up, and before the guest is told anything.
            //
            // The previous owner is resolved while this op is still Finalizing. Once it is Booked,
            // `GetBookedOperationForBookingAsync` (newest Booked wins) returns THIS operation, the
            // identity guard below skips, and the old slot leaks forever.
            var previousOwner = await opRepo.GetBookedOperationForBookingAsync(
                tenantId, booking.BookingId, ct2);

            // Is the booking actually MOVING?
            //
            // A same-slot amendment (a party-size change) derives the identical slot key, so the
            // seat is not being handed from one slot to another — it is being resized in place, and
            // ownership of it has to pass to this operation. `ReserveCapacityCommandHandler` asks
            // the same question from the other side and reserves only the difference, so the two
            // are halves of one rule and read the same two facts to stay in step.
            var previousHold = previousOwner is not null && previousOwner.OperationId != operationId
                ? await holdRepo.GetLatestByOperationAndStepAsync(
                    tenantId, previousOwner.OperationId, "create", ct2)
                : null;

            var (requestedCapacity, capacityIsExplicit, targetSlotId) =
                CapacitySlotKey.DeriveFromCanonicalPayload(revision.CanonicalPayload);

            var sameSlot = previousHold is { Status: HoldStatus.Finalized }
                           && previousHold.SlotId is not null
                           && previousHold.SlotId == targetSlotId;

            // Handing the seat on requires knowing where it WENT. "Not the same slot" is not that.
            //
            // A payload carrying no slot dimension derives a NULL key, which means "not
            // first-party tracked" — not "a different slot". `POST /api/Bookings/{id}/modifications`
            // stores `SubmitModificationInput` as its canonical payload, and that type has no
            // `starts_at`, no `party_size` and no resource/service/location field, so EVERY
            // modification accepted through that door derives `(1, null)` here. With only
            // `!sameSlot` guarding the release below, each one reserved nothing of its own and then
            // released the live booking's FINALIZED hold as `Superseded`: the seats of a confirmed
            // booking went back on sale while the guest still held the table.
            //
            // So the release is withheld in exactly one case: a previous owner sitting on a REAL
            // first-party slot, with no idea where the booking went. Keeping the original seat is
            // the right way round to fail there — an operator can reconcile a seat that is still
            // held, but a table resold underneath a confirmed guest is a double-booking nobody can
            // undo. Everything else is unchanged: when the previous owner holds no first-party slot
            // (an untracked venue, or an external-only hold) there is nothing to lose and the
            // release stays the no-op it always was.
            var movedToKnownSlot = !sameSlot
                                   && (targetSlotId is not null || previousHold?.SlotId is null);

            // The size the slot must end up carrying for this booking. `AbsorbSameSlotAsync` takes
            // it as the AUTHORITATIVE total, so a defaulted 1 does not mean "unspecified" there —
            // it means "resize this booking down to one". `party_size` is optional and may arrive
            // as null or as a TriState string, so an amendment that never mentioned it shrank a
            // party of four to one and put three phantom seats back on sale. An amendment that does
            // not restate the size keeps the size the booking already has.
            var targetCapacity = capacityIsExplicit || previousHold is null
                ? requestedCapacity
                : previousHold.ReservedCapacity;

            // ── Acquire EVERY capacity row this transaction will touch, in canonical order. ──
            //
            // Two different cycles have been built out of this lane, and both came from acquiring
            // rows lazily — each step taking what it needed, in whatever order it happened to need
            // it:
            //
            //   * hold↔slot. `FinalizeAgainstOperationAsync` takes this operation's hold then the
            //     slot; `AbsorbSameSlotAsync` then reaches for the PREVIOUS owner's hold. A
            //     concurrent cancel takes those same two rows the other way round.
            //   * slot↔slot. A cross-slot reschedule takes the NEW slot (via finalize) and then the
            //     OLD one (via the release below) — ordered by role. Two reschedules moving in
            //     opposite directions between the same pair of slots each hold one and want the
            //     other. Locking holds before slots does nothing about this one: both rows are
            //     slots.
            //
            // Taking all four up front collapses both. Every later step re-touches a row this
            // transaction already holds, so it acquires nothing new and can re-order nothing.
            // `ICapacityLockOrder` owns the order (holds before slots, each ascending by id) — this
            // call site only says which rows.
            //
            // LOCKS, not writes. The `HoldNoLongerValid` branch immediately below must leave the old
            // seat untouched; releasing it early would strip the guest of the only capacity they
            // still hold. A lock changes nothing, so ordering and refusal are satisfiable together.
            var ownHold = await holdRepo.GetLatestByOperationAndStepAsync(
                tenantId, operationId, "create", ct2);

            await lockOrder.AcquireAsync(
                tenantId,
                holdIds: [previousHold?.HoldId, ownHold?.HoldId],
                slotIds: [previousHold?.SlotId, targetSlotId],
                ct2);

            var finalizeOutcome = await capacityReservation.FinalizeAgainstOperationAsync(
                tenantId, operationId, "create", ct2);

            if (finalizeOutcome == FinalizeOutcome.HoldNoLongerValid)
            {
                // The new slot is gone — the hold expired or was released before we got here — but
                // the provider has ALREADY been asked to move the booking. There is an external
                // effect that outlived its capacity, and no automatic rule resolves that: honouring
                // the move overbooks the new slot, undoing it takes a seat back from a guest who
                // was already moved.
                //
                // Routed to Reconciling, NOT CompensationRequired. CompensateOperationCommandHandler
                // resolves the provider reference from the "finalize" step, and this lane writes
                // "modify" — so it would find no reference, take its no-effect branch, terminalize
                // as `failed_no_effect` and tell the guest the change did not happen while the
                // venue had already applied it. Reconciling is the §11-legal edge that reaches a
                // human: ReconcileOperationCommandHandler escalates every modify/reschedule to
                // ManualReview, and ResolveManualReviewCommandHandler settles the booking's
                // ModificationPending either way the operator decides.
                //
                // Do NOT release the old slot: the guest must not end up holding neither seat.
                await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, freshOp.Version,
                    OperationStatus.Finalizing, OperationStatus.Reconciling,
                    reason: "modify_hold_no_longer_valid", ct: ct2);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "booking.modify_hold_no_longer_valid",
                    Actor = "system",
                    SubjectRef = booking.BookingId,
                    CorrelationId = operationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new { reason = "hold_no_longer_valid" }),
                    OccurredAt = now,
                }, ct2);

                return;
            }

            // The booking must still be LIVE. Re-read inside the transaction, because the check at
            // the top of Handle ran before the provider call and a cancellation can commit in that
            // window — cancel only moves the BOOKING's lifecycle, never the creating operation's
            // status, so `GetBookedOperationForBookingAsync` still resolves an owner and nothing
            // else here would notice. Without this the modify booked itself against a cancelled
            // booking, finalized a hold nothing would ever release, and told the guest their
            // booking had moved moments after they cancelled it.
            //
            // Stated as an ALLOW-LIST of the states an amendment may settle from, not as a
            // block-list of `Cancelled`. `CancellationPending` is what `POST .../cancellations`
            // parks while a cancellation is in flight, and it passed the old check — so a modify
            // could finalize as Booked, settle capacity and tell the guest their booking moved
            // while another operation was actively removing it. It takes the same disposition as
            // `Cancelled`: reconcile, and let a human decide which of the two actually happened.
            // A lifecycle value added later fails closed here rather than slipping through.
            var bookingNow = await bookingRepo.GetByIdAsync(tenantId, booking.BookingId, ct2);
            var amendable = bookingNow is
            {
                LifecycleStatus: BookingLifecycleStatus.Confirmed
                        or BookingLifecycleStatus.ModificationPending
            };

            if (!amendable)
            {
                var blockedReason = bookingNow is null
                    ? "modify_target_missing_in_flight"
                    : bookingNow.LifecycleStatus == BookingLifecycleStatus.Cancelled
                        ? "modify_target_cancelled_in_flight"
                        : "modify_target_cancellation_in_flight";

                await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, freshOp.Version,
                    OperationStatus.Finalizing, OperationStatus.Reconciling,
                    reason: blockedReason, ct: ct2);

                // The hold this call just finalized belongs to an amendment that must not stand.
                if (finalizeOutcome == FinalizeOutcome.Finalized)
                {
                    await capacityReservation.ReleaseFinalizedByOperationAsync(
                        tenantId, operationId, ReleaseReason.Compensated, ct2);
                }

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "booking.modify_target_cancelled_in_flight",
                    Actor = "system",
                    SubjectRef = booking.BookingId,
                    CorrelationId = operationId,
                    OperationId = operationId,
                    // The event type is kept stable for anything already querying it; the exact
                    // state that blocked the amendment travels in the metadata.
                    Metadata = JsonSerializer.Serialize(new
                    {
                        reason = blockedReason,
                        lifecycle = bookingNow?.LifecycleStatus.ToString(),
                    }),
                    OccurredAt = now,
                }, ct2);

                return;
            }

            // ── The same-slot seat transfer is decided BEFORE the operation is called Booked. ──
            //
            // It has to be: Booked is terminal, so a capacity failure discovered afterwards has
            // nowhere legal to go. Running it after the transition meant a refusal was swallowed
            // into a counter while the operation still settled Booked, cleared ModificationPending
            // and told the guest the booking had changed — with the ledger still holding the old
            // size against a provider reservation that had already grown.
            if (sameSlot
                && !await capacityReservation.AbsorbSameSlotAsync(
                    tenantId, previousOwner!.OperationId, operationId, "create",
                    previousHold!.SlotId!, targetCapacity, ct2))
            {
                // The slot cannot carry the new size and nothing was written. The provider has
                // already applied the amendment, so this is the same shape as an expired hold:
                // there is an external effect the ledger cannot honour, and only a human can
                // decide between resizing the venue's own capacity and moving the guest back.
                metrics.FulfillmentCapacityRaceLossTotal.Add(1);

                await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, freshOp.Version,
                    OperationStatus.Finalizing, OperationStatus.Reconciling,
                    reason: "modify_capacity_unavailable", ct: ct2);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "booking.modify_capacity_unavailable",
                    Actor = "system",
                    SubjectRef = booking.BookingId,
                    CorrelationId = operationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new { reason = "capacity_unavailable" }),
                    OccurredAt = now,
                }, ct2);

                return;
            }

            // Finalized, or NotTracked for a venue that has declared no slots — capacity is
            // unenforced there by decision, not by accident. Either way the amendment stands.
            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, freshOp.Version,
                OperationStatus.Finalizing, OperationStatus.Booked,
                reason: reason, ct: ct2);

            // Lost the race — another delivery is settling it. Do no dependent writes.
            if (rows == 0)
            {
                // But the finalize above already ran, and this transaction is about to COMMIT it.
                //
                // Deciding capacity before the transition is deliberate (the hold's validity has to
                // be known before the guest is told anything), and it is safe when the winner also
                // booked: their finalize is the identical idempotent write. It is NOT safe when the
                // winner routed the operation somewhere else — a concurrent delivery whose provider
                // call came back ambiguous moves it to Reconciling — because compensation and
                // release paths only ever touch ACTIVE holds, so a hold this loser flipped to
                // Finalized would never be given back.
                //
                // Only undo what this call actually did: a hold that was ALREADY Finalized before
                // we ran belongs to the winner and must be left alone.
                // NOT applied to the same-slot path: there the absorb above has already handed the
                // booking's whole seat to this operation, so releasing it would strip a live
                // booking of the only capacity it has. The absorb is idempotent and the winning
                // delivery performs the identical one, so leaving it is consistent either way.
                var settled = await opRepo.GetAsync(operationId, ct2);
                if (!sameSlot
                    && finalizeOutcome == FinalizeOutcome.Finalized
                    && settled is not null
                    && settled.Status != OperationStatus.Booked)
                {
                    await capacityReservation.ReleaseFinalizedByOperationAsync(
                        tenantId, operationId, ReleaseReason.Compensated, ct2);
                }

                return;
            }

            // `NotTracked` is not a secured destination — the SAME distinction the manual lane
            // makes, kept here so the two cannot drift.
            //
            // `HoldNoLongerValid` already returned above, so the outcomes reaching here are
            // Finalized or NotTracked, and only the first means this operation actually holds the
            // new seat. NotTracked means no first-party hold was ever created — the destination slot
            // was never declared — while a cross-slot payload still derives a non-null
            // `targetSlotId`, so `movedToKnownSlot` was true and the previous owner's FINALIZED hold
            // was released anyway: the booking committed Booked with capacity on neither slot, and
            // the seat the guest actually occupies went back on sale.
            //
            // Only the release is gated, and only where there is a real first-party seat to lose —
            // a previous owner holding none has nothing to give up, so an untracked venue behaves
            // exactly as before.
            var destinationSecuredOrNothingToLose =
                finalizeOutcome == FinalizeOutcome.Finalized
                || previousHold is not { Status: HoldStatus.Finalized, SlotId: not null };

            if (movedToKnownSlot
                && destinationSecuredOrNothingToLose
                && previousOwner is not null
                && previousOwner.OperationId != operationId)
            {
                // `Superseded`, not `ProviderReleased`: the seat is not being given back to the
                // venue, it is being handed from the old slot to the new one by this very
                // operation. Cancel uses ProviderReleased because there the booking really is gone.
                await capacityReservation.ReleaseFinalizedByOperationAsync(
                    tenantId, previousOwner.OperationId, ReleaseReason.Superseded, ct2);
            }

            // A modification accepted through `POST /api/Bookings/{id}/modifications` parks the
            // booking at ModificationPending so concurrent writers see the change in flight. That
            // front door is the ONLY writer of that status and nothing cleared it, so before this
            // the booking stayed "modification pending" forever — the operation reported `booked`
            // while every reader of the booking row was told a change was still outstanding.
            // Cancel has always closed its own pending state (CancellationPending → Cancelled);
            // this is the same discipline for the modify lane.
            //
            // Re-read inside the transaction: the version moved if the amendment raced anything
            // else, and a stale token would silently no-op. Only ModificationPending is cleared —
            // a booking Cancelled underneath us must stay cancelled.
            //
            // And only THIS operation's own pending state. `ModificationPending` has one writer,
            // `POST /api/Bookings/{id}/modifications`, which always creates a `Modify` — a
            // RESCHEDULE rides the spec-04 acceptance door and parks nothing, so a reschedule
            // finding the booking pending is looking at somebody else's in-flight modification.
            // Clearing it there announced "no change outstanding" while that modification was
            // still running. `BookingPendingLifecycle` owns the operation-type → parked-state map
            // so the success and failure paths cannot disagree about who parked what.
            var freshBooking = await bookingRepo.GetByIdAsync(tenantId, booking.BookingId, ct2);
            if (BookingPendingLifecycle.PendingStateFor(freshOp) == BookingLifecycleStatus.ModificationPending
                && freshBooking is { LifecycleStatus: BookingLifecycleStatus.ModificationPending })
            {
                // Losing this race is benign and needs no branch: the only other writer of this
                // row is settling the same booking, and the operation transition above already
                // carried the authoritative outcome.
                await bookingRepo.TryTransitionLifecycleAsync(
                    tenantId, freshBooking.BookingId, freshBooking.Version,
                    BookingLifecycleStatus.Confirmed, now, ct2);
            }

            // Otherwise the booking's lifecycle is deliberately untouched. A modified booking is
            // still confirmed; what moved lives on the new intent revision, which the read
            // projection already joins to the operation.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                // A pre-order operation signals its SAGA, which owns the combined outcome and the
                // item compensation; a direct guest notification here would skip both and tell the
                // guest the booking moved while its items were never committed (spec 11 §5.3).
                // FinalizeBookingCommandHandler and ReconcileOperationCommandHandler branch the
                // same way; this lane was the only one that did not.
                MessageType = freshOp.IsPreorder
                    ? Cordia.Application.Handlers.Preorder.PreorderSagaMessages.BookingCommitted
                    : ModifyConfirmedEventType,
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                    booking_id = booking.BookingId,
                }),
                OccurredAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "booking.modified",
                Actor = "system",
                SubjectRef = booking.BookingId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new { reason }),
                OccurredAt = now,
            }, ct2);

            committed = true;
        }, ct);

        // Emitted AFTER the transaction returns, not inside it. A counter increment is not
        // transactional: if the commit failed after the delegate ran — an outbox or audit unique
        // violation, a dropped connection — the operation would stay Finalizing while the counter
        // had already recorded a terminal `booked`, and the redelivery that follows would count it
        // a second time. Keyed on `committed`, which is only set on the path that actually wrote,
        // so a lost race (rows == 0) contributes nothing either.
        if (committed)
        {
            metrics.FulfillmentOperationTerminalTotal.Add(
                1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "booked"));
        }

        return committed ? StepOutcome.Terminal : StepOutcome.Continue;
    }

    /// <summary>
    /// Ambiguity is a first-class state (Inv-7): the reconciler establishes what the provider
    /// actually did before anything is reported or retried.
    /// </summary>
    private async Task<StepOutcome> RouteToReconcilingAsync(
        string tenantId, string operationId, ProviderResultKind kind, CancellationToken ct)
    {
        return await RouteToTerminalAsync(
            tenantId, operationId, OperationStatus.Reconciling, $"modify_{kind}".ToLowerInvariant(), ct);
    }

    private async Task<StepOutcome> RouteToTerminalAsync(
        string tenantId, string operationId, OperationStatus next, string reason, CancellationToken ct)
    {
        // Assert the edge against the §11 set before writing it. `TryTransitionStatusAsync` checks
        // status + version, never legality, so an illegal target silently succeeds and the state
        // machine stops being the authoritative description of what the engine does — which is how
        // Finalizing→Rejected got written here in the first place. Every route out of this handler
        // funnels through here, so one assertion covers them all.
        OperationStateMachine.EnsureAllowed(OperationStatus.Finalizing, next);

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var fresh = await opRepo.GetAsync(operationId, ct2);
            if (fresh is null || fresh.Status != OperationStatus.Finalizing)
                return;

            await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, fresh.Version,
                OperationStatus.Finalizing, next, reason: reason, ct: ct2);
        }, ct);

        return StepOutcome.Continue;
    }
}
