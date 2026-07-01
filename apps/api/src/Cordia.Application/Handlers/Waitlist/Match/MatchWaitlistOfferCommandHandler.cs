using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Waitlist.Match;

/// <summary>
/// Matches an opening to the next eligible enrollee and makes a time-boxed offer (spec 11 §5.1).
///
/// <para><b>Runs inside the inbox transaction</b> — never opens its own. Steps (all in one tx):
/// find the FIFO-first eligible enrollee; reserve a temporary capacity hold (spec 07); insert the
/// offer (Matched) and advance it to Offered with an expiry; schedule the offer-expiry job; and
/// enqueue the offer notification. If the enrollee has a satisfiable <see cref="AutoBookGrant"/>
/// (§20) the offer is auto-accepted instead: the hold is finalized and a booking-confirmed event is
/// emitted — no customer offer round-trip.</para>
///
/// <para><b>Settles (no-op) without throwing</b> when there is no eligible enrollee or no capacity
/// (NoCapacity / Ambiguous), so the message is not redelivered into a poison loop.</para>
/// </summary>
public sealed class MatchWaitlistOfferCommandHandler(
    IWaitlistEnrollmentRepository enrollmentRepo,
    IWaitlistOfferRepository offerRepo,
    ICapacityReservation capacity,
    OfferExpiryScheduler offerExpiry,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<MatchWaitlistOfferCommandHandler> logger)
    : IRequestHandler<MatchWaitlistOfferCommand, Unit>
{
    /// <summary>Outbox event: a deadline-bound offer was sent to the customer.</summary>
    public const string OfferNotifiedEventType = "waitlist.offer.notified.v1";

    /// <summary>Outbox event: an offer auto-booked under a recorded grant produced a confirmed booking.</summary>
    public const string BookingConfirmedEventType = "waitlist.booking_confirmed.v1";

    /// <summary>
    /// Workflow step the waitlist capacity hold is reserved under. This is deliberately the SAME step
    /// the spec 06 engine reserves under (<c>create</c>) so that when an accepted offer is driven
    /// through spec 04 → 06, the engine's idempotency pre-check (keyed on
    /// tenant + operation + workflow_step) finds and REUSES this existing hold instead of reserving a
    /// second one (spec 11 §5.1, divergence #1 — single booking front door, no parallel capacity path).
    /// </summary>
    public const string WorkflowStep = "create";

    public async Task<Unit> Handle(MatchWaitlistOfferCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var criteria = new WaitlistMatchCriteria(
            command.LocationId, command.ServiceId, command.SlotId, command.SlotStartAt, command.AvailableSeats);

        var eligible = await enrollmentRepo.FindEligibleAsync(criteria, ct);
        if (eligible.Count == 0)
        {
            logger.LogInformation(
                "Waitlist match: no eligible enrollee for slot {SlotId} @ {SlotStart}; settling.",
                command.SlotId, command.SlotStartAt);
            return Unit.Value;
        }

        var enrollment = eligible[0];
        var operationId = $"op_wl_{Guid.NewGuid():N}";
        var expiresAt = now.AddSeconds(enrollment.OfferLifetimeSeconds);

        var outcome = await capacity.ReserveAsync(
            new ReserveRequest(
                command.TenantId, operationId, WorkflowStep, command.SlotId,
                enrollment.PartySize, expiresAt, "first_party"),
            ct);

        if (outcome.Status != ReservationStatus.Held || outcome.Hold is null)
        {
            logger.LogInformation(
                "Waitlist match: no capacity ({Status}) for slot {SlotId}; settling.",
                outcome.Status, command.SlotId);
            return Unit.Value; // NoCapacity / Ambiguous — no offer; fail-secure settle.
        }

        var offerId = $"wlo_{Guid.NewGuid():N}";
        await offerRepo.AddAsync(new WaitlistOffer
        {
            TenantId = command.TenantId,
            OfferId = offerId,
            EnrollmentId = enrollment.EnrollmentId,
            HoldId = outcome.Hold.HoldId,
            OperationId = operationId,
            SlotId = command.SlotId,
            SlotStartAt = command.SlotStartAt,
            AvailableSeats = command.AvailableSeats,
            Status = WaitlistOfferStatus.Matched,
            MatchedAt = now,
            CreatedAt = now,
        }, ct);

        // Matched → Offered (version 0 → 1 on the freshly-inserted row).
        await offerRepo.TryOfferAsync(command.TenantId, offerId, expectedVersion: 0, now, expiresAt, ct);

        var grant = enrollment.GetAutoBookGrant();
        var deposit = enrollment.GetDepositTerms();
        if (grant is not null && grant.Permits(enrollment.PartySize, command.SlotStartAt, deposit))
        {
            // Auto-book under recorded consent (§20): finalize the hold and confirm directly.
            await capacity.FinalizeAgainstOperationAsync(command.TenantId, operationId, WorkflowStep, ct);
            await offerRepo.TryResolveAsync(
                command.TenantId, offerId, expectedVersion: 1,
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted, now, ct);

            await EnqueueAsync(command.TenantId, BookingConfirmedEventType, offerId, enrollment, now, new
            {
                offer_id = offerId,
                enrollment_id = enrollment.EnrollmentId,
                operation_id = operationId,
                slot_start_at = command.SlotStartAt,
                auto_booked = true,
            }, ct);
            await AuditAsync(command.TenantId, "waitlist.auto_booked", offerId, operationId, now, ct);
            return Unit.Value;
        }

        // Normal path: schedule the expiry and notify the customer of the deadline-bound offer.
        await offerExpiry.ScheduleAsync(command.TenantId, offerId, expiresAt, ct);
        await EnqueueAsync(command.TenantId, OfferNotifiedEventType, offerId, enrollment, now, new
        {
            offer_id = offerId,
            enrollment_id = enrollment.EnrollmentId,
            customer_ref = enrollment.CustomerRef,
            notification_channel = enrollment.NotificationChannel,
            offer_expires_at = expiresAt,
            slot_start_at = command.SlotStartAt,
        }, ct);
        await AuditAsync(command.TenantId, "waitlist.offered", offerId, operationId, now, ct);
        return Unit.Value;
    }

    private Task EnqueueAsync(
        string tenantId, string messageType, string offerId, WaitlistEnrollment enrollment,
        DateTimeOffset now, object payload, CancellationToken ct)
        => outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = messageType,
            CorrelationId = enrollment.EnrollmentId,
            Payload = JsonSerializer.Serialize(payload),
            OccurredAt = now,
        }, ct);

    private Task AuditAsync(
        string tenantId, string eventType, string offerId, string operationId, DateTimeOffset now, CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = offerId,
            CorrelationId = offerId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(new { offer_id = offerId, operation_id = operationId }),
            OccurredAt = now,
        }, ct);
}
