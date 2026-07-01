using System.Text.Json;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Waitlist;

/// <summary>
/// Builds the deterministic <see cref="SubmitConfirmedIntentCommand"/> that turns a matched waitlist
/// offer into an official confirmed booking through the SAME spec-04 front door a direct booking uses
/// (spec 11 §5.1, divergence #1). Shared by the customer-accept path
/// (<see cref="Accept.AcceptWaitlistOfferCommandHandler"/>) and the platform auto-book path
/// (<see cref="AutoAccept.AutoAcceptWaitlistOfferCommandHandler"/>) so there is exactly one place that
/// mints a waitlist booking — no parallel implementation.
///
/// <para>All identifiers derive from the offer id, so every retry/replay is byte-identical and spec-04
/// replays instead of duplicating (Inv-5). The canonical hash is computed exactly as spec 04 does
/// (generated converter settings) so the §8.8 confirmation-hash check passes. The confirmation is
/// bound to the enrolled customer's captured identity/verification (Inv-1) — never to whoever merely
/// saw the offer notification.</para>
/// </summary>
public static class WaitlistAcceptSubmission
{
    /// <summary>
    /// Builds the submit command for accepting <paramref name="offer"/> under <paramref name="enrollment"/>.
    /// When <paramref name="autoBookConsentEvidenceId"/> is supplied (auto-book, §20), the recorded
    /// consent evidence is stamped into the confirmation disclosures so the minted revision is an
    /// auditable record of the consent that authorized the silent booking.
    /// </summary>
    public static SubmitConfirmedIntentCommand Build(
        string tenantId,
        WaitlistOffer offer,
        WaitlistEnrollment enrollment,
        ICanonicalRequestHasher hasher,
        DateTimeOffset now,
        string? autoBookConsentEvidenceId = null)
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
            // §20: an auto-book is only permitted under recorded, constraint-scoped consent — record the
            // consent evidence id on the confirmation so the minted revision is auditable. Empty for a
            // customer-initiated accept (the accept itself is the verified follow-up).
            DisclosuresPresented = autoBookConsentEvidenceId is { Length: > 0 }
                ? [$"auto_book_consent:{autoBookConsentEvidenceId}"]
                : new List<string>(),
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
    public static ChannelEnum MapChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "email" => ChannelEnum.Email,
        "sms" => ChannelEnum.Sms,
        "voice" => ChannelEnum.Voice,
        _ => ChannelEnum.Web,
    };
}
