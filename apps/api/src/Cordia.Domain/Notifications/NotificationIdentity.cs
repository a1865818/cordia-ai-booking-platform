using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>
/// Derives a stable, deterministic notification identity key used for idempotent dedup (Inv-9).
/// Same inputs always produce the same id; any material change yields a new id.
/// </summary>
public static class NotificationIdentity
{
    // Maps each NotificationEvent to its stable pg/json string label.
    // MUST cover every NotificationEvent member: Derive indexes it directly, and a missing entry
    // crashes the send INSIDE the inbox transaction (poison loop → DLQ) for that event type —
    // exactly how the spec-11 WaitlistOfferExpired sends silently died. An exhaustive unit test
    // (NotificationIdentityTests) pins full coverage against the enum.
    private static readonly IReadOnlyDictionary<NotificationEvent, string> EventLabels =
        new Dictionary<NotificationEvent, string>
        {
            [NotificationEvent.RequestAccepted] = "request_accepted",
            [NotificationEvent.StillPending] = "still_pending",
            [NotificationEvent.ActionRequired] = "action_required",
            [NotificationEvent.BookingConfirmed] = "booking_confirmed",
            [NotificationEvent.NoCapacityAlternatives] = "no_capacity_alternatives",
            [NotificationEvent.WaitlistOffer] = "waitlist_offer",
            [NotificationEvent.WaitlistOfferExpired] = "waitlist_offer_expired",
            [NotificationEvent.PreorderCancelled] = "preorder_cancelled",
            [NotificationEvent.FinalFailure] = "final_failure",
            [NotificationEvent.CancelOrModifyConfirmed] = "cancel_or_modify_confirmed",
        };

    /// <summary>
    /// Deterministically derives the notification identity for a delivery row.
    /// Returns <c>"ntf_"</c> + lowercase SHA-256 hex of
    /// <c>"{tenantId}|{operationId}|{eventLabel}|{recipientRef}|{templateVersion}|{materialRevision}"</c>.
    /// </summary>
    public static string Derive(
        string tenantId,
        string operationId,
        NotificationEvent eventType,
        string recipientRef,
        string templateVersion,
        int materialRevision)
    {
        var eventLabel = EventLabels[eventType];
        var input = global::System.Text.Encoding.UTF8.GetBytes(
            $"{tenantId}|{operationId}|{eventLabel}|{recipientRef}|{templateVersion}|{materialRevision}");
        var hash = global::System.Security.Cryptography.SHA256.HashData(input);
        return "ntf_" + global::System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}
