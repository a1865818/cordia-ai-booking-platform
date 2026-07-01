using System.Text.Json;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Accept;

/// <summary>
/// Accepts a live waitlist offer by routing it through the SAME confirmed-booking front door a direct
/// booking uses (spec 04 <c>SubmitConfirmedIntentCommand</c>) — spec 11 §5.1, divergence #1.
///
/// <para>Historically the accept finalized its own capacity hold and emitted a bespoke
/// <c>waitlist.booking_confirmed.v1</c> event: a parallel booking path in which merely resolving the
/// offer minted the booking, so anyone who saw the offer notification could effectively book for the
/// enrollee. This handler instead builds a canonical <see cref="BookingRequest"/> from the enrollment
/// (+ the matched slot), computes its canonical hash, stamps a <see cref="Confirmation"/> with the
/// enrolled customer's captured identity/verification context, and submits it through spec 04. Spec 04
/// mints the official <c>booking_intent_revisions</c> / <c>intent_confirmations</c> /
/// <c>booking_operations</c> records under the offer's operation id (Inv-1) and hands the booking to
/// the spec 06 engine, which drives capacity (07) / payment (09) / provider-create and emits the
/// single confirmed outcome. The waitlist match reserves its capacity hold under the 06 workflow step
/// (<c>create</c>) so the 06 engine transparently reuses it instead of double-reserving.</para>
///
/// <para>Ordering is record-before-state and self-healing: the confirmed intent is submitted BEFORE the
/// offer is claimed <c>Offered → Accepted</c>. Because the spec 04 submit is idempotent on a
/// deterministic per-offer key, every path (fresh accept, concurrent accept, retry after a crash, and
/// an already-Accepted replay) re-submits safely and never mints a duplicate booking.</para>
///
/// <para>Idempotent (Inv-5): an already-Accepted offer re-submits (04 replays) and returns. A
/// non-Offered (and non-Accepted) offer throws <see cref="InvalidWaitlistOfferTransitionException"/>
/// (409); an unknown offer throws <see cref="WaitlistOfferNotFoundException"/> (404).</para>
///
/// <para><b>Identity (divergence #1, open dependency):</b> the minted confirmation is attributed to the
/// enrollment's captured <see cref="WaitlistEnrollment.IdentityRef"/> / verification level, so the
/// booking is bound to the enrolled customer — not to whoever opened the notification. Enforcing that
/// the <em>caller</em> of the accept endpoint IS that customer ("verified follow-up", §5.1) is the
/// spec 08 identity / manage-booking concern and is NOT closed here.</para>
/// </summary>
public sealed class AcceptWaitlistOfferCommandHandler(
    IWaitlistOfferRepository offerRepo,
    IWaitlistEnrollmentRepository enrollmentRepo,
    ICanonicalRequestHasher hasher,
    IMediator mediator,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<AcceptWaitlistOfferCommand, Unit>
{
    public async Task<Unit> Handle(AcceptWaitlistOfferCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var offer = await offerRepo.GetAsync(command.OfferId, ct);
        if (offer is null || offer.TenantId != command.TenantId)
            throw new WaitlistOfferNotFoundException(command.OfferId);

        // Reject a terminal/unexpected state up front (409). An already-Accepted offer is idempotent
        // and still re-submits below so a crash between submit and claim self-heals.
        if (offer.Status != WaitlistOfferStatus.Accepted)
            WaitlistOfferStateMachine.EnsureAllowed(offer.Status, WaitlistOfferStatus.Accepted);

        var enrollment = await enrollmentRepo.GetAsync(offer.EnrollmentId, ct)
            ?? throw new WaitlistEnrollmentNotFoundException(offer.EnrollmentId);

        if (offer.SlotStartAt is null || string.IsNullOrEmpty(offer.OperationId))
            throw new InvalidOperationException(
                $"Waitlist offer '{offer.OfferId}' cannot be accepted: missing matched slot or operation id.");

        // --- Build the confirmed booking submission (spec 04) from the enrollment + matched slot. ---
        var submit = BuildSubmitCommand(command.TenantId, offer, enrollment, now);

        // Idempotent replay path: the offer is already Accepted (concurrent winner or retry). Re-submit
        // to 04 (same key → replay, no duplicate) so a crash after the claim still lands the booking.
        if (offer.Status == WaitlistOfferStatus.Accepted)
        {
            await mediator.Send(submit, ct);
            return Unit.Value;
        }

        // --- Record-before-state: mint the official confirmed booking BEFORE claiming the offer. ---
        // Spec 04 is idempotent on submit.Request.IdempotencyKey, so concurrent accepts and retries
        // converge on a single booking regardless of who wins the claim below.
        await mediator.Send(submit, ct);

        // --- Claim the offer Offered → Accepted (version-guarded). ---
        var won = await offerRepo.TryResolveAsync(
            command.TenantId, command.OfferId, offer.Version,
            WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted, now, ct);
        if (won == 0)
        {
            // Lost the race: a concurrent accept/expire moved the row. Reload and re-evaluate.
            var reloaded = await offerRepo.GetAsync(command.OfferId, ct);
            if (reloaded?.Status == WaitlistOfferStatus.Accepted)
                return Unit.Value; // concurrent accept won — same booking already minted (idempotent)
            throw new InvalidWaitlistOfferTransitionException(
                reloaded?.Status ?? offer.Status, WaitlistOfferStatus.Accepted);
        }

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "waitlist.accepted",
                Actor = command.Actor,
                SubjectRef = offer.OfferId,
                CorrelationId = offer.OfferId,
                OperationId = offer.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    offer_id = offer.OfferId,
                    operation_id = offer.OperationId,
                    enrollment_id = offer.EnrollmentId,
                    identity_ref = enrollment.IdentityRef,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }

    /// <summary>
    /// Assembles the deterministic <see cref="SubmitConfirmedIntentCommand"/> for accepting
    /// <paramref name="offer"/>. All identifiers derive from the offer id so every retry/replay is
    /// byte-identical (Inv-5). The canonical hash is computed exactly as spec 04 does (generated
    /// converter settings) so the §8.8 confirmation-hash check passes.
    /// </summary>
    private SubmitConfirmedIntentCommand BuildSubmitCommand(
        string tenantId, WaitlistOffer offer, WaitlistEnrollment enrollment, DateTimeOffset now)
    {
        var payload = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = enrollment.ServiceId,
            LocationId = enrollment.LocationId,
            PartySize = enrollment.PartySize,
            StartsAt = offer.SlotStartAt!.Value,
        };

        var payloadNode = JsonSerializer.SerializeToNode(payload, Cordia.Domain.Generated.Converter.Settings);
        var hash = hasher.Hash(payloadNode);

        var intentId = $"wl_int_{offer.OfferId}";

        var revision = new IntentRevision
        {
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = payload,
            PayloadHash = hash.Value,
            SchemaVersion = string.Empty,
            TenantPolicyVersion = string.Empty,
            SourceSessionId = offer.EnrollmentId,
            CreatedAt = now,
        };

        var confirmation = new Confirmation
        {
            ConfirmationId = $"wl_conf_{offer.OfferId}",
            IntentId = intentId,
            IntentRevision = 1,
            // BARE hash — spec 04 checks confirmation.intent_payload_hash == computed H.Value (§8.8).
            IntentPayloadHash = hash.Value,
            ConfirmationChannel = MapChannel(enrollment.NotificationChannel),
            // Divergence #1: bind the booking to the enrolled customer's captured identity (Inv-1).
            Identity = enrollment.IdentityRef,
            VerificationLevel = enrollment.VerificationLevel,
            ConfirmedAt = now,
        };

        var request = new SubmitConfirmedIntentRequest
        {
            // Deterministic per-offer key so re-accept / retry replays instead of duplicating (Inv-5).
            IdempotencyKey = $"wl_accept_{offer.OfferId}",
            // Mint the operation under the id the capacity hold was reserved against so the 06 engine
            // reuses the existing "create" hold (see MatchWaitlistOfferCommandHandler.WorkflowStep).
            OperationId = offer.OperationId!,
            IntentId = intentId,
            IntentRevision = 1,
            Revision = revision,
            Confirmation = confirmation,
        };

        return new SubmitConfirmedIntentCommand(tenantId, request);
    }

    /// <summary>
    /// Maps the enrollment's free-form <c>notification_channel</c> to the closed
    /// <see cref="ChannelEnum"/> confirmation-channel vocabulary. Unknown values fall back to
    /// <see cref="ChannelEnum.Web"/> (the generic online channel a link-based accept arrives on).
    /// </summary>
    private static ChannelEnum MapChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "email" => ChannelEnum.Email,
        "sms" => ChannelEnum.Sms,
        "voice" => ChannelEnum.Voice,
        _ => ChannelEnum.Web,
    };
}
