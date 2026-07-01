using System.Text.Json;
using Cordia.Application.Handlers.Waitlist.AutoAccept;
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
/// (§20) the offer is auto-booked instead: rather than finalizing capacity or emitting a bespoke
/// event here (Inv-1), it enqueues the auto-accept step, which mints an auditable confirmed revision
/// through the same spec-04 front door a customer accept uses — no customer offer round-trip.</para>
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

        // M7 / Inv-6: FindEligibleAsync already filters cancelled enrollments, but a cancel may have
        // committed in the read→reserve window. Re-check before reserving anything so we never take a
        // hold (or make an offer) for a just-cancelled enrollment.
        var fresh = await enrollmentRepo.GetAsync(enrollment.EnrollmentId, ct);
        if (fresh is null || fresh.CancelledAt is not null)
        {
            logger.LogInformation(
                "Waitlist match: enrollment {EnrollmentId} cancelled before reserve; settling.", enrollment.EnrollmentId);
            return Unit.Value;
        }
        enrollment = fresh;

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
            // Auto-book under recorded consent (§20, Inv-1): route through the SAME spec-04 front door a
            // customer accept uses — do NOT finalize capacity or emit a bespoke booking event here. The
            // offer stays Offered; the auto-accept step (a plain consumer that can open its own
            // transaction to call spec-04) mints an auditable confirmed revision carrying the grant's
            // consent evidence and claims Offered → Accepted, after which the 06 engine finalizes the
            // "create" hold. Schedule the offer-expiry job as the BACKSTOP: if the auto-accept step never
            // books (grant no longer permits at re-verify, or the message is permanently undeliverable),
            // ExpireWaitlistOfferCommandHandler releases the hold + re-matches — so the hold and offer can
            // never leak. If auto-accept books first, the offer is Accepted and the expiry job no-ops.
            await offerExpiry.ScheduleAsync(command.TenantId, offerId, expiresAt, ct);
            await EnqueueAsync(command.TenantId, AutoAcceptWaitlistOfferCommand.MessageType, offerId, enrollment, now, new
            {
                offer_id = offerId,
                operation_id = operationId,
            }, ct);
            await AuditAsync(command.TenantId, "waitlist.auto_book_scheduled", offerId, operationId, now, ct);
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
