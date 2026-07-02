using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// In-process template resolver stub used for development. Returns a deterministic
/// <see cref="NotificationTemplate"/> for each event type, channel, and locale combination.
/// Template versions are stable strings that form part of the notification identity (Inv-9) —
/// a template change here produces new notification ids.
///
/// <para>Per-tenant template store, A/B variants, and locale-specific wording are
/// open decisions (spec 10 §31). Inv-12: no PAN/CVV appears in any body template;
/// <c>ActionRequired</c> carries only a payment-link placeholder, never card data.</para>
/// </summary>
public sealed class NotificationTemplateResolver : INotificationTemplateResolver
{
    public Task<NotificationTemplate> ResolveAsync(
        NotificationEvent eventType,
        NotificationChannel channel,
        string locale,
        CancellationToken ct = default)
    {
        var eventLabel = EventLabel(eventType);
        var channelLabel = ChannelLabel(channel);
        var templateVersion = $"{eventLabel}.{channelLabel}.v1";
        var bodyTemplate = BodyTemplate(eventType);
        return Task.FromResult(new NotificationTemplate(templateVersion, bodyTemplate));
    }

    private static string EventLabel(NotificationEvent eventType) => eventType switch
    {
        NotificationEvent.RequestAccepted => "request_accepted",
        NotificationEvent.StillPending => "still_pending",
        NotificationEvent.ActionRequired => "action_required",
        NotificationEvent.BookingConfirmed => "booking_confirmed",
        NotificationEvent.NoCapacityAlternatives => "no_capacity_alternatives",
        NotificationEvent.WaitlistOffer => "waitlist_offer",
        NotificationEvent.WaitlistOfferExpired => "waitlist_offer_expired",
        NotificationEvent.FinalFailure => "final_failure",
        NotificationEvent.CancelOrModifyConfirmed => "cancel_or_modify_confirmed",
        _ => eventType.ToString().ToLowerInvariant(),
    };

    private static string ChannelLabel(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Sms => "sms",
        NotificationChannel.Email => "email",
        NotificationChannel.Channel => "channel",
        _ => channel.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Returns a safe body template for the given event type.
    /// Inv-12: no PAN/CVV or raw card data; ActionRequired carries only a payment-link placeholder.
    /// §17.5: StillPending uses the truthful wording — "no booking or charge should be assumed
    /// until we send confirmation."
    /// </summary>
    private static string BodyTemplate(NotificationEvent eventType) => eventType switch
    {
        NotificationEvent.RequestAccepted =>
            "We've received your request and are processing it. You'll hear back shortly.",
        NotificationEvent.StillPending =>
            "We're still confirming your request — no booking or charge should be assumed until we send confirmation.",
        NotificationEvent.ActionRequired =>
            "Action required: please complete your payment using the secure link provided. Do not share this link. {payment_link}",
        NotificationEvent.BookingConfirmed =>
            "Your booking is confirmed. Thank you for your reservation.",
        NotificationEvent.NoCapacityAlternatives =>
            "Unfortunately we couldn't secure your first-choice slot. Please review the alternatives provided.",
        NotificationEvent.WaitlistOffer =>
            "Good news — a spot opened up! Your waitlist offer expires at {offer_expires_at}. Reply to confirm.",
        NotificationEvent.WaitlistOfferExpired =>
            "Your waitlist offer has expired and the table was released. You're still on the waitlist for the next opening.",
        NotificationEvent.FinalFailure =>
            "We were unable to complete your booking. Our team will follow up with you shortly.",
        NotificationEvent.CancelOrModifyConfirmed =>
            "Your cancellation/modification has been confirmed.",
        _ => "Your booking status has been updated.",
    };
}
