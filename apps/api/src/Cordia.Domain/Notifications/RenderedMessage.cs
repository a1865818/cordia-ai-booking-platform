using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>
/// The rendered, ready-to-send message payload. Carries no secrets (Inv-12):
/// <see cref="MinimizedRecipient"/> must be a masked/tokenised reference, never raw card data.
/// </summary>
/// <param name="MinimizedRecipient">
/// Masked/tokenised reference — safe to log and persist. Never the raw contact value.
/// </param>
/// <param name="Destination">
/// The real delivery address the provider adapter sends to (E.164 phone or email), or
/// <c>null</c> when the operation has no guest. Kept separate from
/// <paramref name="MinimizedRecipient"/> precisely so the loggable/persisted handle stays
/// free of contact details while the transport still has somewhere to deliver.
/// Adapters MUST NOT log this value.
/// </param>
public sealed record RenderedMessage(
    string TemplateVersion,
    NotificationChannel Channel,
    string Locale,
    string Body,
    string MinimizedRecipient,
    string? Destination = null);
