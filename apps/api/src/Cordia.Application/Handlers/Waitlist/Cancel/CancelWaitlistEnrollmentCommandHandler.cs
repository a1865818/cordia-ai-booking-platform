using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Cancel;

/// <summary>
/// Cancels a waitlist enrollment in one transaction (spec 11 §4.4, Inv-6): stamps the enrollment
/// cancelled (version-guarded, terminal) and releases any live offer — rolling back its temporary
/// capacity hold so the slot is freed. Idempotent: a re-cancel of an already-cancelled enrollment
/// is a no-op. Throws <see cref="WaitlistEnrollmentNotFoundException"/> (404) for an unknown id.
/// </summary>
public sealed class CancelWaitlistEnrollmentCommandHandler(
    IWaitlistEnrollmentRepository enrollmentRepo,
    IWaitlistOfferRepository offerRepo,
    ICapacityReservation capacity,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<CancelWaitlistEnrollmentCommand, Unit>
{
    public async Task<Unit> Handle(CancelWaitlistEnrollmentCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var enrollment = await enrollmentRepo.GetAsync(command.EnrollmentId, ct2);
            if (enrollment is null || enrollment.TenantId != command.TenantId)
                throw new WaitlistEnrollmentNotFoundException(command.EnrollmentId);

            // Idempotent: already cancelled → nothing to do (Inv-6 terminal).
            if (enrollment.CancelledAt is not null)
                return;

            await enrollmentRepo.CancelAsync(
                command.TenantId, command.EnrollmentId, enrollment.Version, now, ct2);

            // Release any live offer + roll back its temporary hold so the slot is freed. Resolve the
            // offer FIRST (version-guarded) and release the hold ONLY when we win: a concurrent accept
            // finalizing this same offer/hold would move the row, so we'd lose the claim (0 rows) and
            // must NOT release capacity out from under the winning booking (Inv-6).
            var offer = await offerRepo.GetLiveByEnrollmentAsync(command.EnrollmentId, ct2);
            if (offer is not null)
            {
                var released = await offerRepo.TryResolveAsync(
                    command.TenantId, offer.OfferId, offer.Version, offer.Status,
                    WaitlistOfferStatus.Released, now, ct2);

                if (released > 0 && !string.IsNullOrEmpty(offer.HoldId))
                    await capacity.ReleaseAsync(command.TenantId, offer.HoldId, ReleaseReason.Compensated, ct2);
            }

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "waitlist.cancelled",
                Actor = command.Actor,
                SubjectRef = command.EnrollmentId,
                CorrelationId = command.EnrollmentId,
                OperationId = null,
                Metadata = JsonSerializer.Serialize(new { released_offer_id = offer?.OfferId }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }
}
