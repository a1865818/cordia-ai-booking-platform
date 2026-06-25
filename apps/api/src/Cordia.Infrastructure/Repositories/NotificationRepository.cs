using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="INotificationRepository"/>.</summary>
public class NotificationRepository(ApplicationDbContext db) : INotificationRepository
{
    public async Task AddAsync(Notification notification, CancellationToken ct = default)
    {
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
    }

    public Task<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
        => db.Notifications
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.NotificationId == notificationId, ct);

    public Task<int> TryTransitionStateAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Notification>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.NotificationId == notificationId,
            s => s.SetProperty(x => x.State, next),
            ct);
}
