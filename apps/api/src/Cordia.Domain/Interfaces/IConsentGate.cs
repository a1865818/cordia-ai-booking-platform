using Cordia.Domain.Enums;
using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Checks whether a tenant's recipient has opted in to receive notifications on a channel.</summary>
public interface IConsentGate
{
    Task<ConsentDecision> CheckAsync(
        string tenantId,
        string recipientRef,
        NotificationChannel channel,
        CancellationToken ct = default);
}
