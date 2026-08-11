using System.Text.Json;
using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Waitlist.AutoAccept;

/// <summary>
/// Auto-books a waitlist offer under a recorded <c>AutoBookGrant</c> by routing it through the SAME
/// spec-04 confirmed-booking front door a customer accept uses (spec 11 §5.1, §20, Inv-1). Closes the
/// last auto-book divergence: instead of finalizing capacity + emitting the bespoke
/// <c>waitlist.booking_confirmed.v1</c>, it mints an auditable confirmed revision + confirmation that
/// records the grant's <c>ConsentEvidenceId</c>, so a silent auto-book is a first-class, evidenced
/// booking rather than a parallel path.
///
/// <para>Queue-driven and self-healing. It re-verifies the grant still permits the matched slot (§20 —
/// never auto-book outside recorded consent); if consent is no longer satisfiable it settles without
/// booking (the capacity hold and offer expire naturally). Ordering is record-before-state: the
/// spec-04 submit (idempotent on <c>wl_accept_{offerId}</c>) runs before the version-guarded
/// <c>Offered → Accepted</c> claim, so a crash between the two self-heals on redelivery (Inv-5).</para>
/// </summary>
public sealed class AutoAcceptWaitlistOfferCommandHandler(
    IWaitlistOfferRepository offerRepo,
    IWaitlistEnrollmentRepository enrollmentRepo,
    ICanonicalRequestHasher hasher,
    IMediator mediator,
    IAuditEventRepository auditRepo,
    WaitlistAcceptReconcileScheduler reconcileScheduler,
    IUnitOfWork uow,
    ILogger<AutoAcceptWaitlistOfferCommandHandler> logger)
    : IRequestHandler<AutoAcceptWaitlistOfferCommand, Unit>
{
    public async Task<Unit> Handle(AutoAcceptWaitlistOfferCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var offer = await offerRepo.GetAsync(command.OfferId, ct);
        if (offer is null || offer.TenantId != command.TenantId)
        {
            logger.LogWarning("Waitlist auto-accept: offer {OfferId} not found; settling.", command.OfferId);
            return Unit.Value;
        }

        // Only Offered (fresh) or Accepted (idempotent replay) proceed; any other state means a
        // concurrent expire/decline won — settle without booking (never book on a dead offer).
        if (offer.Status is not (WaitlistOfferStatus.Offered or WaitlistOfferStatus.Accepted))
        {
            logger.LogWarning(
                "Waitlist auto-accept: offer {OfferId} is {Status}, not Offered; settling.",
                command.OfferId, offer.Status);
            return Unit.Value;
        }

        if (offer.SlotStartAt is null || string.IsNullOrEmpty(offer.OperationId))
        {
            logger.LogWarning(
                "Waitlist auto-accept: offer {OfferId} missing matched slot / operation id; settling.", command.OfferId);
            return Unit.Value;
        }

        var enrollment = await enrollmentRepo.GetAsync(offer.EnrollmentId, ct);
        if (enrollment is null)
        {
            logger.LogWarning(
                "Waitlist auto-accept: enrollment {EnrollmentId} for offer {OfferId} not found; settling.",
                offer.EnrollmentId, command.OfferId);
            return Unit.Value;
        }

        // Inv-6 / M7: never auto-book a cancelled enrollment — settle without booking (the offer-expiry
        // backstop releases the hold + re-matches).
        if (enrollment.CancelledAt is not null)
        {
            logger.LogWarning(
                "Waitlist auto-accept: enrollment {EnrollmentId} is cancelled; settling without booking.",
                offer.EnrollmentId);
            return Unit.Value;
        }

        // §20: re-verify the recorded consent still covers this matched slot. If the grant was removed
        // or no longer permits (party/window/deposit), DO NOT auto-book — settle fail-secure.
        var grant = enrollment.GetAutoBookGrant();
        var deposit = enrollment.GetDepositTerms();
        if (grant is null || !grant.Permits(enrollment.PartySize, offer.SlotStartAt.Value, deposit))
        {
            logger.LogWarning(
                "Waitlist auto-accept: offer {OfferId} no longer satisfies the auto-book grant; settling without booking.",
                command.OfferId);
            return Unit.Value;
        }

        // Mint the confirmed revision through spec-04, stamping the consent evidence (§20/Inv-1).
        var submit = WaitlistAcceptSubmission.Build(
            command.TenantId, offer, enrollment, hasher, now, grant.ConsentEvidenceId);

        // Idempotent replay: already Accepted (a prior delivery won) — re-submit (04 replays) and return.
        if (offer.Status == WaitlistOfferStatus.Accepted)
        {
            await mediator.Send(submit, ct);
            return Unit.Value;
        }

        // Record-before-state: mint the booking BEFORE claiming the offer so a crash self-heals.
        await mediator.Send(submit, ct);

        // The claim, the reconcile schedule and the audit commit TOGETHER — the same fix the
        // customer accept lane needed, and this path needs it more.
        //
        // With the claim committing on its own, a failure before the follow-up transaction left the
        // offer permanently Accepted with no reconcile scheduled and no `waitlist.auto_booked`
        // record. The retry cannot repair it: the already-Accepted branch above re-submits and
        // returns without ever reaching this code. Nothing else schedules that reconcile, so the
        // enrollment is never marked fulfilled — and because `FindEligibleAsync` excludes only
        // enrollments with a LIVE offer (Accepted is not live), the enrollee stays eligible
        // forever. Under a standing AutoBookGrant the next opening books them a SECOND table with
        // no new consent, which is the outcome §20 exists to prevent. The 5–15 minute residual
        // double-offer window becomes unbounded.
        var won = 0;
        var reconcileOnly = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await offerRepo.TryResolveAsync(
                command.TenantId, command.OfferId, offer.Version,
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted, now, ct2);

            if (won == 0)
            {
                var reloaded = await offerRepo.GetAsync(command.OfferId, ct2);
                if (reloaded?.Status == WaitlistOfferStatus.Accepted)
                    return; // concurrent auto-accept won — same booking already minted (idempotent)

                // Lost to a concurrent expire/decline after the booking was minted (rare). The spec-04
                // record stands (06 owns its outcome), so schedule a reconcile that settles the
                // orphaned operation against the enrollment — booked → mark fulfilled so no re-offer;
                // failed → stays eligible.
                reconcileOnly = true;
                await reconcileScheduler.ScheduleAsync(
                    command.TenantId, command.OfferId, offer.OperationId!, offer.EnrollmentId,
                    attempt: 0, now, ct2);
                return;
            }

            // Same as the customer accept: settle the enrollment off the real 06 outcome. Once the
            // booking lands, the reconcile marks the enrollment fulfilled so the enrollee can never be
            // matched (and silently auto-booked) a second table inside their acceptance window.
            await reconcileScheduler.ScheduleAsync(
                command.TenantId, command.OfferId, offer.OperationId!, offer.EnrollmentId, attempt: 0, now, ct2);
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "waitlist.auto_booked",
                Actor = "system",
                SubjectRef = offer.OfferId,
                CorrelationId = offer.OfferId,
                OperationId = offer.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    offer_id = offer.OfferId,
                    operation_id = offer.OperationId,
                    enrollment_id = offer.EnrollmentId,
                    identity_ref = enrollment.IdentityRef,
                    // §20 audit: the recorded consent that authorized this silent booking.
                    consent_evidence_id = grant.ConsentEvidenceId,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        if (reconcileOnly)
        {
            logger.LogWarning(
                "Waitlist auto-accept: offer {OfferId} moved on after the booking was minted; reconcile scheduled.",
                command.OfferId);
        }

        return Unit.Value;
    }
}
