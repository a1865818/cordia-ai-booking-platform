using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>
/// The rendered, ready-to-send message payload. Carries no secrets (Inv-12):
/// <see cref="MinimizedRecipient"/> must be a masked/tokenised reference, never raw card data.
/// </summary>
public sealed record RenderedMessage(
    string TemplateVersion,
    NotificationChannel Channel,
    string Locale,
    string Body,
    string MinimizedRecipient);
