using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
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

    public Task<Notification?> GetAsync(string tenantId, string notificationId, CancellationToken ct = default)
        => db.Notifications
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.NotificationId == notificationId, ct);

    public Task<int> TryTransitionStateAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        NotificationState next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Notification>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.NotificationId == notificationId && e.State == expectedState,
            s => s
                .SetProperty(x => x.State, next)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT … ON CONFLICT (tenant_id, notification_id) DO NOTHING</c> so that a
    /// duplicate logical event does NOT abort the surrounding transaction (unlike a bare unique
    /// violation). All enum columns receive explicit pg-enum casts to avoid "unknown type" errors
    /// when the Npgsql type-mapper has not yet seen the enum on this connection.
    /// Returns 1 (inserted / new identity) or 0 (dedup hit / identity already present).
    /// </remarks>
    public async Task<bool> TryInsertAsync(Notification notification, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.notifications
                (tenant_id, notification_id, operation_id, event_type, recipient_ref,
                 template_version, material_revision, state, channel, priority,
                 provider_idempotency_key, provider_ref, failure_category,
                 attempt_count, next_eligible_at, created_at, updated_at, version)
            VALUES
                ({notification.TenantId}, {notification.NotificationId}, {notification.OperationId},
                 {PgEnum.Label(notification.EventType)}::notification_event_enum,
                 {notification.RecipientRef}, {notification.TemplateVersion}, {notification.MaterialRevision},
                 {PgEnum.Label(notification.State)}::notification_state_enum,
                 {PgEnum.Label(notification.Channel)}::notification_channel_enum,
                 {PgEnum.Label(notification.Priority)}::message_priority_enum,
                 {notification.ProviderIdempotencyKey}, {notification.ProviderRef}, {notification.FailureCategory},
                 {notification.AttemptCount}, {notification.NextEligibleAt},
                 {notification.CreatedAt}, {notification.UpdatedAt}, {notification.Version})
            ON CONFLICT (tenant_id, notification_id) DO NOTHING
            """,
            ct);

        return rows == 1;
    }

    /// <inheritdoc/>
    public Task<int> MarkAcceptedAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        string? providerRef,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Notification>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.NotificationId == notificationId && e.State == expectedState,
            s => s
                .SetProperty(x => x.State, NotificationState.Accepted)
                .SetProperty(x => x.ProviderRef, providerRef)
                .SetProperty(x => x.FailureCategory, (string?)null)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Notification>> ListStuckSendingAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken ct = default)
        => await db.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(n => n.State == NotificationState.Sending && n.UpdatedAt < olderThan)
            .OrderBy(n => n.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public Task<int> MarkFailedAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        NotificationState next,
        string? failureCategory,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Notification>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.NotificationId == notificationId && e.State == expectedState,
            s => s
                .SetProperty(x => x.State, next)
                .SetProperty(x => x.FailureCategory, failureCategory)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);

    /// <inheritdoc/>
    public Task<int> MarkTransientRetryAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        DateTimeOffset nextEligibleAt,
        string? failureCategory,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Notification>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.NotificationId == notificationId && e.State == expectedState,
            s => s
                .SetProperty(x => x.State, NotificationState.FailedTransient)
                .SetProperty(x => x.NextEligibleAt, nextEligibleAt)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.FailureCategory, failureCategory)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);

    // Terminal statuses that require BookingConfirmed (§F4).
    private static readonly OperationStatus[] BookedStatuses =
    [
        OperationStatus.Booked,
    ];

    // Terminal statuses that require FinalFailure (§F4).
    private static readonly OperationStatus[] FailureStatuses =
    [
        OperationStatus.FailedFinal,
        OperationStatus.Rejected,
        OperationStatus.Expired,
    ];

    // Pre-built accepted/delivered state filter — reused in the subqueries below.
    private static readonly NotificationState[] AcceptedStates =
    [
        NotificationState.Accepted,
        NotificationState.Delivered,
    ];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TerminalOperationRef>> ListTerminalOperationsMissingAcceptedNotificationAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken ct = default)
    {
        // Build cross-tenant sets of (tenantId, operationId) for each expected terminal notification type.
        // IgnoreQueryFilters() bypasses the global tenant filter so we see all tenants.
        // Waitlisted ops are EXCLUDED from the sweep (no notify expectation — waitlisted.v1 is not registered).
        var acceptedConfirmed = db.Notifications
            .IgnoreQueryFilters()
            .Where(n => AcceptedStates.Contains(n.State) && n.EventType == NotificationEvent.BookingConfirmed)
            .Select(n => new { n.TenantId, n.OperationId });

        var acceptedFailure = db.Notifications
            .IgnoreQueryFilters()
            .Where(n => AcceptedStates.Contains(n.State) && n.EventType == NotificationEvent.FinalFailure)
            .Select(n => new { n.TenantId, n.OperationId });

        return await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(op =>
                op.UpdatedAt < olderThan &&
                (
                    (op.Status == OperationStatus.Booked &&
                        !acceptedConfirmed.Any(a => a.TenantId == op.TenantId && a.OperationId == op.OperationId)) ||
                    ((op.Status == OperationStatus.FailedFinal ||
                      op.Status == OperationStatus.Rejected ||
                      op.Status == OperationStatus.Expired) &&
                        !acceptedFailure.Any(a => a.TenantId == op.TenantId && a.OperationId == op.OperationId))
                ))
            .OrderBy(op => op.UpdatedAt)
            .Take(limit)
            .Select(op => new TerminalOperationRef(op.TenantId, op.OperationId, op.Status))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task<bool> HasAcceptedTerminalNotificationAsync(
        string tenantId,
        string operationId,
        NotificationEvent expectedEvent,
        CancellationToken ct = default)
        => db.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(n =>
                n.TenantId == tenantId &&
                n.OperationId == operationId &&
                n.EventType == expectedEvent &&
                (n.State == NotificationState.Accepted || n.State == NotificationState.Delivered),
                ct);
}
