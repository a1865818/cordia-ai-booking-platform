using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="Notification"/> aggregate.</summary>
public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken ct = default);

    Task<Notification?> GetAsync(string notificationId, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional transition: sets <paramref name="next"/> state and increments
    /// version where the current version equals <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = success, 0 = lost race → caller should reload).
    /// </summary>
    Task<int> TryTransitionStateAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState next,
        CancellationToken ct = default);
}
