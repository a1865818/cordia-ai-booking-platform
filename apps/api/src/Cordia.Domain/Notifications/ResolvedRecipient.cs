using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>A validated recipient with resolved channel and locale.</summary>
public sealed record ResolvedRecipient(
    string RecipientRef,
    NotificationChannel Channel,
    string Locale);
