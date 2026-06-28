using Cordia.Domain.Enums;
using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Port for a channel-specific notification delivery provider.</summary>
public interface INotificationProviderAdapter
{
    NotificationChannel Channel { get; }

    bool SupportsSendIdempotency { get; }

    bool SupportsLookup { get; }

    Task<NotificationSendResult> SendAsync(
        string providerIdempotencyKey,
        RenderedMessage message,
        CancellationToken ct = default);

    Task<NotificationSendResult?> LookupByKeyAsync(
        string providerIdempotencyKey,
        CancellationToken ct = default);
}
