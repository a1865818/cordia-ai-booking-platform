using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;

/// <summary>
/// Handles capacity reservation for a booking operation (spec 07 §6.1, T5).
///
/// Parses the canonical payload from <c>booking_intent_revisions.canonical_payload</c> to
/// derive the first-party slot key (<see cref="CapacitySlotKey.Derive"/>). If the slot exists
/// in <c>capacity_slots</c>, performs an atomic conditional reserve via
/// <see cref="ICapacityReservation.ReserveAsync"/> and enqueues a hold-expiry job
/// inside the SAME transaction (Inv-4/Inv-6). If the slot is not provisioned, transitions
/// directly to Held without a durable hold (availability mode = live; preserves current behavior
/// for un-provisioned resources).
///
/// Routes:
///   Tracked + <see cref="ReservationStatus.Held"/>         → ReservingCapacity → Held (reason "held") + expiry job.
///   Tracked + <see cref="ReservationStatus.NoCapacity"/>   → ReservingCapacity → AlternativesRequired
///                                                             (FM#8, reason "no_capacity") + outbox + audit.
///   Tracked + <see cref="ReservationStatus.Ambiguous"/>    → ReservingCapacity → Reconciling (reason "reserve_ambiguous").
///   NOT tracked (no slot row)                               → ReservingCapacity → Held (reason "held", no hold/expiry).
/// </summary>
public sealed class ReserveCapacityCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    ICapacityReservation capacityReservation,
    ICapacitySlotRepository slotRepo,
    ICapacityHoldRepository holdRepo,
    IBookingIntentRepository intentRepo,
    CapacityExpiryScheduler expiryScheduler,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow,
    IOptions<CapacityOptions> capacityOptions,
    Metrics metrics)
    : IRequestHandler<ReserveCapacityCommand, StepOutcome>
{
    public async Task<StepOutcome> Handle(ReserveCapacityCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.ReservingCapacity)
            return StepOutcome.Continue; // already advanced past this step

        var operationId = command.OperationId;
        var tenantId = command.TenantId;

        // Parse canonical payload BEFORE the transaction: stateless + no DB write.
        // Any missing/unparseable field is treated as "non-tracked" (see below).
        var rev = await intentRepo.GetRevisionAsync(tenantId, op.IntentId, op.IntentRevision, ct);
        var (requestedCapacity, capacityIsExplicit, slotId) =
            CapacitySlotKey.DeriveFromCanonicalPayload(rev?.CanonicalPayload);

        // An amendment that does not MOVE the booking reserves only what is genuinely NEW.
        //
        // The slot key deliberately excludes party size, so a modify that changes only the party
        // size derives the SAME slot the booking already occupies. Reserving the full new size
        // against it asks the slot to carry the booking twice, and a slot with no spare room
        // refuses — landing the operation in AlternativesRequired and telling the guest there is no
        // space for a table they are already sitting at.
        //
        // The other half of this rule is `ICapacityReservation.AbsorbSameSlotAsync`, which
        // `ModifyBookingCommandHandler` calls at commit: it hands the previous owner's seat to this
        // operation and sizes the result to the whole booking, so exactly ONE finalized hold
        // survives. Reserving a delta WITHOUT that absorb would leave two, and a later cancellation
        // releases only the newest — which is why neither half is correct on its own.
        var previousCapacity = await ReadSameSlotCapacityAsync(op, slotId, ct);

        // An amendment that does not RESTATE the party size does not change it.
        //
        // `party_size` is optional in the shared schema and can arrive absent, as an explicit null,
        // or as a TriState string — all of which derive the fallback of 1. Taken as the new size,
        // an amendment to a party of four computed a delta of `1 - 4 = -3` here and handed three
        // seats back to the venue that the guests were still sitting in. The default belongs to a
        // CREATE, which has no current size to keep; here the booking's own contribution is the
        // truth unless the payload says otherwise. `AbsorbSameSlotAsync` at commit is sized from
        // the same rule, so the two halves stay in step.
        var targetCapacity = capacityIsExplicit || previousCapacity is null
            ? requestedCapacity
            : previousCapacity.Value;

        var capacityToReserve = previousCapacity is null
            ? targetCapacity
            : targetCapacity - previousCapacity.Value;

        var expiresAt = DateTimeOffset.UtcNow + capacityOptions.Value.HoldTtl;

        StepOutcome result = StepOutcome.Continue;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // 1. Create-or-skip the "reserve_capacity" workflow step.
            var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, "reserve_capacity", ct2);
            if (existingStep is null)
            {
                var now2 = DateTimeOffset.UtcNow;
                await stepRepo.AddAsync(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    StepType = "reserve_capacity",
                    Status = WorkflowStepStatus.Succeeded,
                    AttemptCount = 0,
                    Version = 0,
                    CreatedAt = now2,
                    UpdatedAt = now2,
                }, ct2);
            }

            // 2. Re-read op inside txn; bail if not in the expected state (lost-race guard).
            var current = await opRepo.GetAsync(operationId, ct2);
            if (current is null || current.TenantId != tenantId || current.Status != OperationStatus.ReservingCapacity)
            {
                result = StepOutcome.Continue;
                return;
            }

            // 3. Determine if first-party-tracked: slotId derived AND slot row exists.
            //    A same-slot amendment needing no extra units takes the untracked branch on
            //    purpose: there is nothing to reserve, and the commit's absorb is what gives this
            //    operation ownership of the seat the booking already holds.
            var isTracked = slotId is not null
                && capacityToReserve > 0
                && await slotRepo.GetAsync(tenantId, slotId, ct2) is not null;

            if (isTracked)
            {
                // 4a. Tracked path — perform atomic conditional reserve.
                var outcome = await capacityReservation.ReserveAsync(
                    new ReserveRequest(
                        tenantId,
                        operationId,
                        "create",
                        slotId,
                        capacityToReserve,
                        expiresAt,
                        "first_party"),
                    ct2);

                switch (outcome.Status)
                {
                    case ReservationStatus.Held:
                        // Enqueue expiry job inside the SAME txn (Inv-4).
                        await expiryScheduler.ScheduleAsync(
                            tenantId,
                            operationId,
                            outcome.Hold!.HoldId,
                            outcome.Hold.ExpiresAt,
                            ct2);

                        // Transition ReservingCapacity → Held.
                        _ = await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, current.Version,
                            OperationStatus.ReservingCapacity, OperationStatus.Held,
                            reason: "held", ct: ct2);

                        metrics.FulfillmentHoldAcquiredTotal.Add(1);
                        metrics.FulfillmentAcceptedToHeldMs.Record((DateTimeOffset.UtcNow - op.CreatedAt).TotalMilliseconds);
                        result = StepOutcome.Continue;
                        break;

                    case ReservationStatus.NoCapacity:
                        // FM#8 — no capacity: advance to AlternativesRequired.
                        var rows = await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, current.Version,
                            OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
                            reason: "no_capacity", ct: ct2);

                        if (rows > 0)
                        {
                            var now = DateTimeOffset.UtcNow;

                            // Spec 11 §5.3 / Inv-8: a pre-order operation must NOT send the customer a raw
                            // alternatives notification and hang in this 06 wait state — no saga signal would
                            // reach it and the reserved items would dangle. Instead signal the saga
                            // (booking_failed, reason no_capacity) so it compensates the reserved items and
                            // emits the ONE combined outcome. A normal operation keeps the customer-facing
                            // alternatives_required notification.
                            await outboxRepo.AddAsync(new OutboxMessage
                            {
                                TenantId = tenantId,
                                MessageId = $"obx_{Guid.NewGuid():N}",
                                OperationId = operationId,
                                MessageType = current.IsPreorder
                                    ? Cordia.Application.Handlers.Preorder.PreorderSagaMessages.BookingFailed
                                    : "notification.alternatives_required.v1",
                                CorrelationId = operationId,
                                Payload = JsonSerializer.Serialize(new
                                {
                                    operation_id = operationId,
                                    tenant_id = tenantId,
                                    reason = "no_capacity",
                                }),
                                OccurredAt = now,
                            }, ct2);

                            await auditRepo.AddAsync(new AuditEvent
                            {
                                TenantId = tenantId,
                                EventId = $"evt_{Guid.NewGuid():N}",
                                EventType = "operation.alternatives_required",
                                Actor = "system",
                                SubjectRef = operationId,
                                CorrelationId = operationId,
                                OperationId = operationId,
                                Metadata = JsonSerializer.Serialize(new { reason = "no_capacity" }),
                                OccurredAt = now,
                            }, ct2);
                            metrics.FulfillmentCapacityRaceLossTotal.Add(1);
                        }

                        // rows==0 → lost race; return Continue so the driver reloads (C13).
                        result = rows > 0 ? StepOutcome.Wait : StepOutcome.Continue;
                        break;

                    case ReservationStatus.Ambiguous:
                        // External provider returned an ambiguous outcome — route to Reconciling (Inv-7).
                        _ = await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, current.Version,
                            OperationStatus.ReservingCapacity, OperationStatus.Reconciling,
                            reason: "reserve_ambiguous", ct: ct2);

                        result = StepOutcome.Continue;
                        break;
                }
            }
            else
            {
                // 4b. NOT tracked (no capacity_slots row for derived slotId, or no resource/time
                //     dimension in the payload). Transition → Held without a durable hold.
                //     This preserves the current always-available behavior for un-provisioned
                //     resources (availability mode = live).
                //
                // Counted, because this branch used to be completely silent. `capacity_slots` had
                // a repository read and no writer, so the table was empty in every deployment and
                // this path took 100% of traffic: no booking was ever capacity-checked, and
                // nothing in logs, metrics or audit said so — it was discoverable only by reading
                // this handler. The behaviour is unchanged and still correct for an
                // un-provisioned resource; what changes is that "capacity is not being enforced"
                // is now a number a venue can see instead of an assumption.
                //
                // The reason distinguishes the two ways to land here, because they need different
                // fixes: no key at all means the payload lacks a resource/time dimension, while a
                // key with no row means the venue simply has not declared that slot.
                // `already_held` is not an enforcement gap at all — it is a same-slot amendment
                // whose units are already booked under the operation that made the booking, and
                // whose ownership the commit's absorb transfers. Folding it into the other two
                // would report capacity as unenforced every time a guest shrank a party.
                var alreadyHeld = capacityToReserve <= 0;

                var untracked = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.ReservingCapacity, OperationStatus.Held,
                    reason: alreadyHeld ? "held_existing_capacity" : "held", ct: ct2);

                // Counted only when the transition actually happened.
                //
                // Incremented ahead of the write, the counter fired for every attempt — including
                // one that lost the optimistic-concurrency race and advanced nothing — so the one
                // number a venue has for "how many bookings were accepted without capacity
                // enforcement" drifted permanently upward and could never be reconciled against the
                // operations it claimed to describe.
                if (untracked > 0 && !alreadyHeld)
                {
                    metrics.FulfillmentCapacityUntrackedTotal.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            MetricNames.Tags.Reason,
                            slotId is null ? "no_slot_key" : "slot_not_declared"));
                }

                result = StepOutcome.Continue;
            }
        }, ct);

        return result;
    }

    /// <summary>
    /// The units this booking ALREADY holds on <paramref name="slotId"/>, when this operation is an
    /// amendment that is not moving the booking off its current slot — otherwise
    /// <see langword="null"/>, meaning "reserve the full amount" (every create, and every genuine
    /// reschedule to a different slot).
    ///
    /// <para>Read from the finalized hold of the operation that actually booked the slot, which is
    /// the same row <c>ModifyBookingCommandHandler</c> consults before deciding between absorbing
    /// it and releasing it. Both sides ask the identical question — "is the seat moving?" — from the
    /// same two facts, so they cannot answer it differently.</para>
    /// </summary>
    private async Task<int?> ReadSameSlotCapacityAsync(
        BookingOperation op, string? slotId, CancellationToken ct)
    {
        if (slotId is null
            || string.IsNullOrWhiteSpace(op.BookingId)
            || op.OperationType is not (OperationTypeEnum.Modify or OperationTypeEnum.Reschedule))
        {
            return null;
        }

        var owner = await opRepo.GetBookedOperationForBookingAsync(op.TenantId, op.BookingId, ct);
        if (owner is null || owner.OperationId == op.OperationId)
            return null;

        var hold = await holdRepo.GetLatestByOperationAndStepAsync(
            op.TenantId, owner.OperationId, "create", ct);

        return hold is { Status: HoldStatus.Finalized } && hold.SlotId == slotId
            ? hold.ReservedCapacity
            : null;
    }
}
