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
    IBookingOperationRepository opRepo,
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

            // The stamp is the ACTION. Everything below describes it, so nothing below may run
            // unless it happened.
            //
            // `CancelAsync` is version-guarded and filters `CancelledAt == null`, so 0 rows means a
            // concurrent writer cancelled it first. Ignoring that wrote an `allow` decision for
            // `waitlist.enrollment_cancelled` on a call that changed nothing — an authorization
            // trail claiming a second operator succeeded at an action only one of them performed —
            // and released the winner's offer hold on top. The enrollment is cancelled either way;
            // the winner's transaction owns the record of it.
            var stamped = await enrollmentRepo.CancelAsync(
                command.TenantId, command.EnrollmentId, enrollment.Version, now, ct2);

            if (stamped == 0)
                return;

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

                // Has an accept already handed this hold to the 06 engine?
                //
                // `GetLiveByEnrollmentAsync` excludes Accepted, so a CLAIMED offer is already out of
                // reach. But the accept lane is record-before-state: it mints the spec-04 operation
                // FIRST and only then claims `Offered → Accepted`. Between that mint COMMITTING and
                // the claim — and for the whole time a deferred auto-accept sits queued — the offer
                // is still live here while a booking operation for it already exists.
                //
                // NOT a complete fence, and worth being precise about: the mint is written inside
                // `SubmitConfirmedIntentCommandHandler`'s own transaction, so it is invisible to
                // this one until it commits. A cancel landing inside that window still sees no
                // operation and still releases. Closing that too would mean serialising on a row
                // both lanes take before either writes, which is a larger change than the leak
                // justifies — this narrows the exposure from "the whole accept flow, including the
                // entire deferred auto-accept delay" to "one in-flight database transaction".
                //
                // Releasing then pulls the hold out from under an in-flight booking. The 06 engine
                // has already reused that hold (ReserveAsync's idempotency pre-check matches on the
                // same operation id and step), so finalize later sees it Released, returns
                // HoldNoLongerValid, and routes the operation to CompensationRequired — after the
                // provider booking was created. Compensation then has to cancel a real reservation,
                // and if the provider answers ambiguously the operation parks in ManualReview.
                //
                // The operation row's existence is exactly the right signal: the offer carries its
                // `op_wl_*` id from match time, but a booking_operations row appears only when an
                // accept submits through the spec-04 door. So a cancel racing a not-yet-run
                // auto-accept still releases correctly — no booking exists to protect.
                var mintedOperation = string.IsNullOrWhiteSpace(offer.OperationId)
                    ? null
                    : await opRepo.GetAsync(offer.OperationId, ct2);

                // Only an operation still IN FLIGHT owns the seat.
                //
                // A terminal one does not, and retaining for it would be strictly worse than the
                // bug: nothing is coming to release the hold, so the seat would sit reserved until
                // the offer-expiry sweeper reclaimed it, when it could have been freed and
                // re-matched now. `IsTerminal` is spec 06's, not a local list.
                //
                // `Booked` is terminal and therefore falls through to the release — which is a
                // no-op, because a booked operation's hold is FINALIZED and `ReleaseAsync` only
                // touches ACTIVE holds. That is the shape the original code relied on without
                // saying so, and it is why this bug only ever bit an in-flight accept.
                var bookingOwnsTheHold = mintedOperation is not null
                                         && mintedOperation.TenantId == command.TenantId
                                         && !OperationStateMachine.IsTerminal(mintedOperation.Status);

                if (released > 0 && !string.IsNullOrEmpty(offer.HoldId) && !bookingOwnsTheHold)
                {
                    await capacity.ReleaseAsync(command.TenantId, offer.HoldId, ReleaseReason.Compensated, ct2);

                    // Re-match the freed opening to the next eligible enrollee, exactly like
                    // decline/expire — otherwise the slot silently leaves the waitlist queue and
                    // the next enrollee is never offered it (review 2026-07-03, C10).
                    var rematch = WaitlistRematchMessage.Build(command.TenantId, offer, enrollment, now);
                    if (rematch is not null)
                        await outboxRepo.AddAsync(rematch, ct2);
                }
                else if (released > 0 && bookingOwnsTheHold)
                {
                    // The enrollment is still cancelled — the guest asked for that — but the seat
                    // now belongs to the booking the accept minted, and 06 owns its lifecycle:
                    // it releases the hold on compensation, expiry or a later cancellation. No
                    // re-match either: the opening is not free.
                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = command.TenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "waitlist.hold_retained_for_booking",
                        Actor = command.Actor,
                        SubjectRef = command.EnrollmentId,
                        CorrelationId = command.EnrollmentId,
                        OperationId = offer.OperationId,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            offer_id = offer.OfferId,
                            hold_id = offer.HoldId,
                            reason = "accept_already_minted_operation",
                        }),
                        OccurredAt = now,
                    }, ct2);
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
