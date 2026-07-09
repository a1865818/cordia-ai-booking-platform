using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Waitlist.Expire;

/// <summary>
/// Expires an outstanding waitlist offer whose deadline elapsed (spec 11 §5.1).
///
/// <para><b>Runs inside the inbox transaction</b> — never opens its own. Version-guards
/// <c>Offered → Expired</c>, releases the temporary hold (reason <see cref="ReleaseReason.Expired"/>),
/// enqueues a re-match for the freed opening, and emits an <c>waitlist.offer.expired.v1</c> event.</para>
///
/// <para><b>Settles (no-op) without throwing</b> when the offer is missing or no longer Offered
/// (a concurrent accept/decline already resolved it, or a duplicate timer fired) so the message is
/// not redelivered into a poison loop (Inv-5).</para>
/// </summary>
public sealed class ExpireWaitlistOfferCommandHandler(
    IWaitlistOfferRepository offerRepo,
    IWaitlistEnrollmentRepository enrollmentRepo,
    ICapacityReservation capacity,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<ExpireWaitlistOfferCommandHandler> logger)
    : IRequestHandler<ExpireWaitlistOfferCommand, Unit>
{
    /// <summary>Outbox event: an outstanding offer lapsed past its deadline.</summary>
    public const string OfferExpiredEventType = "waitlist.offer.expired.v1";

    public async Task<Unit> Handle(ExpireWaitlistOfferCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var offer = await offerRepo.GetAsync(command.OfferId, ct);
        if (offer is null || offer.TenantId != command.TenantId)
        {
            logger.LogInformation("Waitlist expiry: offer {OfferId} not found; settling.", command.OfferId);
            return Unit.Value;
        }

        if (offer.Status != WaitlistOfferStatus.Offered)
        {
            // Already resolved (accepted/declined/expired) — late or duplicate timer; settle.
            logger.LogInformation(
                "Waitlist expiry: offer {OfferId} is {Status}, not Offered; settling.",
                command.OfferId, offer.Status);
            return Unit.Value;
        }

        var won = await offerRepo.TryResolveAsync(
            command.TenantId, command.OfferId, offer.Version,
            WaitlistOfferStatus.Offered, WaitlistOfferStatus.Expired, now, ct);
        if (won == 0)
        {
            // Lost race to a concurrent accept/decline — settle without re-matching.
            logger.LogInformation(
                "Waitlist expiry: offer {OfferId} resolved concurrently; settling.", command.OfferId);
            return Unit.Value;
        }

        if (!string.IsNullOrEmpty(offer.HoldId))
            await capacity.ReleaseAsync(command.TenantId, offer.HoldId, ReleaseReason.Expired, ct);

        var enrollment = await enrollmentRepo.GetAsync(offer.EnrollmentId, ct);
        var rematch = WaitlistRematchMessage.Build(command.TenantId, offer, enrollment, now);
        if (rematch is not null)
            await outboxRepo.AddAsync(rematch, ct);

        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = command.TenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = OfferExpiredEventType,
            CorrelationId = offer.EnrollmentId,
            OperationId = offer.OperationId,
            Payload = JsonSerializer.Serialize(new
            {
                offer_id = offer.OfferId,
                enrollment_id = offer.EnrollmentId,
                customer_ref = enrollment?.CustomerRef,
                notification_channel = enrollment?.NotificationChannel,
                slot_start_at = offer.SlotStartAt,
            }),
            OccurredAt = now,
        }, ct);

        await auditRepo.AddAsync(new AuditEvent
        {
            TenantId = command.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = "waitlist.offer_expired",
            Actor = "system",
            SubjectRef = offer.OfferId,
            CorrelationId = offer.OfferId,
            OperationId = offer.OperationId,
            Metadata = JsonSerializer.Serialize(new { offer_id = offer.OfferId, re_matched = rematch is not null }),
            OccurredAt = now,
        }, ct);

        return Unit.Value;
    }
}
