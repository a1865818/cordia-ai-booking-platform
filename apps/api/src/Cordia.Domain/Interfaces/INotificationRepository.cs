using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="Notification"/> aggregate.</summary>
public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken ct = default);

    Task<Notification?> GetAsync(string notificationId, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional transition (§6.2): sets <paramref name="next"/> state only when
    /// the row is in <paramref name="expectedState"/> AND at <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = success, 0 = lost race / wrong state → caller should reload).
    /// </summary>
    Task<int> TryTransitionStateAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        NotificationState next,
        CancellationToken ct = default);
}
