using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>A validated recipient with resolved channel and locale.</summary>
/// <param name="RecipientRef">
/// Minimized, tokenised handle. This is what is persisted on the notification row and what
/// keys <c>notification_id</c> dedup — it must never carry the raw contact value (Inv-12
/// hygiene; see <see cref="RenderedMessage.MinimizedRecipient"/>).
/// </param>
/// <param name="Destination">
/// The actual delivery address (E.164 phone or email) the provider would send to, or
/// <c>null</c> when the operation has no guest and only a placeholder handle could be derived.
/// Deliberately NOT persisted on the notification row: it is resolved fresh on each send,
/// retry, and reconcile so a single source of truth stays in one place.
/// </param>
public sealed record ResolvedRecipient(
    string RecipientRef,
    NotificationChannel Channel,
    string Locale,
    string? Destination = null);
