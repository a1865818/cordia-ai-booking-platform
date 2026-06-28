using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
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
    IOptions<CapacityOptions> capacityOptions)
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
        var (requestedCapacity, slotId) = ParseCanonicalPayload(rev?.CanonicalPayload);

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

                            await outboxRepo.AddAsync(new OutboxMessage
                            {
                                TenantId = tenantId,
                                MessageId = $"obx_{Guid.NewGuid():N}",
                                OperationId = operationId,
                                MessageType = "notification.alternatives_required.v1",
                                CorrelationId = operationId,
                                Payload = JsonSerializer.Serialize(new
                                {
                                    operation_id = operationId,
                                    tenant_id = tenantId,
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
                _ = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.ReservingCapacity, OperationStatus.Held,
                    reason: "held", ct: ct2);

                result = StepOutcome.Continue;
            }
        }, ct);

        return result;
    }

    /// <summary>
    /// Parses the canonical payload JSON to extract capacity-reservation fields.
    /// Any missing or unparseable field defaults to a safe value (capacity=1, slotId=null).
    /// A null slotId signals "not first-party-tracked" → non-tracked path.
    /// </summary>
    /// <remarks>
    /// Field extraction rules (from spec 07 canonical payload definition):
    ///   party_size (int)     → requestedCapacity; defaults to 1 if absent/invalid (a booking always needs ≥1).
    ///   resource_id          → preferred resource dimension (first non-empty wins over service_id/location_id).
    ///   service_id           → second choice resource dimension.
    ///   location_id          → fallback resource dimension.
    ///   requested_at (ISO-8601 DateTimeOffset) → slot start time.
    ///   slotId = CapacitySlotKey.Derive(resource, service, location, requestedAt);
    ///            null when any required dimension is missing → not tracked.
    /// </remarks>
    private static (int requestedCapacity, string? slotId) ParseCanonicalPayload(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return (1, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // party_size — default 1 (a booking always needs ≥1 unit).
            var requestedCapacity = 1;
            if (root.TryGetProperty("party_size", out var partySizeEl)
                && partySizeEl.TryGetInt32(out var ps)
                && ps > 0)
            {
                requestedCapacity = ps;
            }

            // Resource dimension: first non-empty of resource_id, service_id, location_id.
            var resourceId = TryGetString(root, "resource_id");
            var serviceId = TryGetString(root, "service_id");
            var locationId = TryGetString(root, "location_id");

            // requested_at — ISO-8601 DateTimeOffset.
            if (!root.TryGetProperty("requested_at", out var requestedAtEl)
                || requestedAtEl.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(requestedAtEl.GetString(), out var requestedAt))
            {
                // Missing or unparseable requested_at → cannot derive a deterministic slot key.
                return (requestedCapacity, null);
            }

            var slotId = CapacitySlotKey.Derive(resourceId, serviceId, locationId, requestedAt);
            return (requestedCapacity, slotId);
        }
        catch (JsonException)
        {
            // Malformed JSON → treat as non-tracked.
            return (1, null);
        }
    }

    private static string? TryGetString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            var val = el.GetString();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        return null;
    }
}
