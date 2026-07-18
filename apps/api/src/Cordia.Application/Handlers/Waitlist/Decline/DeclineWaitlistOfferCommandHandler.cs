using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Decline;

/// <summary>
/// Declines a live waitlist offer in one transaction (spec 11 §5.1): version-guards
/// <c>Offered → Declined</c>, releases the temporary hold (rolling it back so the slot is free),
/// and enqueues a re-match so the next eligible enrollee is offered the opening.
///
/// <para>Idempotent: an already-Declined offer returns without re-releasing. A non-Offered (and
/// non-Declined) offer throws <see cref="InvalidWaitlistOfferTransitionException"/> (409); an
/// unknown offer throws <see cref="WaitlistOfferNotFoundException"/> (404).</para>
/// </summary>
public sealed class DeclineWaitlistOfferCommandHandler(
    IWaitlistOfferRepository offerRepo,
    IWaitlistEnrollmentRepository enrollmentRepo,
    ICapacityReservation capacity,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<DeclineWaitlistOfferCommand, Unit>
{
    public async Task<Unit> Handle(DeclineWaitlistOfferCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var offer = await offerRepo.GetAsync(command.OfferId, ct2);
            if (offer is null || offer.TenantId != command.TenantId)
                throw new WaitlistOfferNotFoundException(command.OfferId);

            if (offer.Status == WaitlistOfferStatus.Declined)
                return; // idempotent

            WaitlistOfferStateMachine.EnsureAllowed(offer.Status, WaitlistOfferStatus.Declined);

            var won = await offerRepo.TryResolveAsync(
                command.TenantId, command.OfferId, offer.Version,
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Declined, now, ct2);
            if (won == 0)
            {
                var reloaded = await offerRepo.GetAsync(command.OfferId, ct2);
                if (reloaded?.Status == WaitlistOfferStatus.Declined)
                    return;
                throw new InvalidWaitlistOfferTransitionException(
                    reloaded?.Status ?? offer.Status, WaitlistOfferStatus.Declined);
            }

            if (!string.IsNullOrEmpty(offer.HoldId))
                await capacity.ReleaseAsync(command.TenantId, offer.HoldId, ReleaseReason.Compensated, ct2);

            var enrollment = await enrollmentRepo.GetAsync(offer.EnrollmentId, ct2);
            var rematch = WaitlistRematchMessage.Build(command.TenantId, offer, enrollment, now);
            if (rematch is not null)
                await outboxRepo.AddAsync(rematch, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "waitlist.declined",
                Actor = command.Actor,
                SubjectRef = offer.OfferId,
                CorrelationId = offer.OfferId,
                OperationId = offer.OperationId,
                Metadata = JsonSerializer.Serialize(new { offer_id = offer.OfferId, re_matched = rematch is not null }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }
}
