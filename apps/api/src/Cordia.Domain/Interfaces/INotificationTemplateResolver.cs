using Cordia.Domain.Enums;
using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Resolves the versioned template for a given event type, channel, and locale.</summary>
public interface INotificationTemplateResolver
{
    Task<NotificationTemplate> ResolveAsync(
        NotificationEvent eventType,
        NotificationChannel channel,
        string locale,
        CancellationToken ct = default);
}
