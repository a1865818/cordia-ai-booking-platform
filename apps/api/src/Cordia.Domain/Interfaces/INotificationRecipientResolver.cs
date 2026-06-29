using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Resolves the validated recipient for a booking operation. Returns <c>null</c> when no valid destination exists.</summary>
public interface INotificationRecipientResolver
{
    Task<ResolvedRecipient?> ResolveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
