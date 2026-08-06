using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Cancel;

/// <summary>
/// Cancels a waitlist enrollment in one transaction (spec 11 §4.4, Inv-6): stamps the enrollment
/// cancelled (version-guarded, terminal), releases any live offer — rolling back its temporary
/// capacity hold so the slot is freed — and re-matches the freed opening to the next eligible
/// enrollee (same recovery as decline/expire, §5.1/FM#1). Idempotent: a re-cancel of an
/// already-cancelled enrollment is a no-op. Throws
/// <see cref="WaitlistEnrollmentNotFoundException"/> (404) for an unknown id.
/// </summary>
public sealed class CancelWaitlistEnrollmentCommandHandler(
    IWaitlistEnrollmentRepository enrollmentRepo,
    IWaitlistOfferRepository offerRepo,
    ICapacityReservation capacity,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IAuditWriter audit,
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
                {
                    await capacity.ReleaseAsync(command.TenantId, offer.HoldId, ReleaseReason.Compensated, ct2);

                    // Re-match the freed opening to the next eligible enrollee, exactly like
                    // decline/expire — otherwise the slot silently leaves the waitlist queue and
                    // the next enrollee is never offered it (review 2026-07-03, C10).
                    var rematch = WaitlistRematchMessage.Build(command.TenantId, offer, enrollment, now);
                    if (rematch is not null)
                        await outboxRepo.AddAsync(rematch, ct2);
                }
            }

            // The spec-14 operator-action ALLOW envelope.
            //
            // `OperatorActionBehavior` records only DENIALS, so without this the trail could prove
            // who was refused and not who succeeded — and the business event below carries no
            // asserted role, reason code or decision, which are exactly the fields the operator
            // contract is written in. Both rows are kept: one is the business fact, this one is the
            // authorization decision. Inside the effect's transaction, so a rolled-back action can
            // never leave an allow behind (spec 14 §3.1 step 5).
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: StaffRole.IsKnown(command.ActorRole) ? command.ActorRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(command.Actor) ? "unknown" : command.Actor,
                    Action: "waitlist.enrollment_cancelled",
                    TargetType: "waitlist_enrollment",
                    TargetId: command.EnrollmentId,
                    Decision: "allow",
                    DataClasses: ["operations"],
                    ReasonCode: string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode,
                    CorrelationId: command.EnrollmentId),
                ct2);

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
