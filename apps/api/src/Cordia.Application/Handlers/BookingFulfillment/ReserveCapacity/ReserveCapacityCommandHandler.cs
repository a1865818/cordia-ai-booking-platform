using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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
        var (requestedCapacity, slotId) = CapacitySlotKey.DeriveFromCanonicalPayload(rev?.CanonicalPayload);

        // KNOWN GAP — a same-slot amendment transiently reserves the booking's own seat twice.
        //
        // `ValidateOperationCommandHandler` routes every non-Cancel type through here, and the slot
        // key deliberately excludes party size, so a modify that changes only the party size
        // derives the SAME slot the booking already occupies and reserves a second, full hold
        // against it. The END state is correct — the commit finalizes this hold and releases the
        // previous owner's, netting out to exactly the new size — but between the two the slot must
        // carry both bookings, and a slot with no spare room cannot: the reserve answers NoCapacity
        // and the operation lands in AlternativesRequired, telling the guest there is no space for a
        // table they are already sitting at.
        //
        // Deliberately NOT patched by reserving only the delta. That fixes the refusal and breaks
        // something worse: the previous owner's finalized hold would have to stay, so the booking
        // would end up holding TWO finalized holds, and a later cancellation releases only the
        // newest booked operation's — permanently leaking the rest of the seat. Closing this
        // properly needs a spec 07 primitive that does not exist yet: an atomic transfer of a
        // FINALIZED hold from one operation to another at a new size, which is the only shape that
        // both avoids the transient double-count and leaves exactly one hold to release later.
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
            var isTracked = slotId is not null
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
                        requestedCapacity,
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
                metrics.FulfillmentCapacityUntrackedTotal.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        MetricNames.Tags.Reason,
                        slotId is null ? "no_slot_key" : "slot_not_declared"));

                _ = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.ReservingCapacity, OperationStatus.Held,
                    reason: "held", ct: ct2);

                result = StepOutcome.Continue;
            }
        }, ct);

        return result;
    }

}
