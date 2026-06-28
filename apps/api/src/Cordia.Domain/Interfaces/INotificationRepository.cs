using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="Notification"/> aggregate.</summary>
public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken ct = default);

    Task<Notification?> GetAsync(string tenantId, string notificationId, CancellationToken ct = default);

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

    /// <summary>
    /// Atomic INSERT … ON CONFLICT (tenant_id, notification_id) DO NOTHING for logical dedup
    /// (Inv-9). Returns <see langword="true"/> when the row was inserted (rows == 1); returns
    /// <see langword="false"/> when the identity already exists (dedup hit, DO NOTHING path).
    /// Mirrors <c>InboxRepository.TryAdmitAsync</c> — all enum columns carry explicit pg-enum casts.
    /// </summary>
    Task<bool> TryInsertAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional update that marks the notification <see cref="NotificationState.Accepted"/>
    /// and stores the provider reference returned by the delivery provider.
    /// Returns rows-affected (1 = success, 0 = lost race).
    /// </summary>
    Task<int> MarkAcceptedAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        string? providerRef,
        CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional update that marks the notification with a failure state and
    /// records the failure category. Does NOT overwrite <c>ProviderRef</c>.
    /// Returns rows-affected (1 = success, 0 = lost race).
    /// </summary>
    Task<int> MarkFailedAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        NotificationState next,
        string? failureCategory,
        CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant scan for rows stuck in <see cref="NotificationState.Sending"/> whose
    /// <c>UpdatedAt</c> is older than <paramref name="olderThan"/> (§12.8 ambiguous-send window).
    /// Uses <c>AsNoTracking().IgnoreQueryFilters()</c> so the global tenant filter is bypassed.
    /// Results are ordered by <c>UpdatedAt</c> ascending; at most <paramref name="limit"/> rows returned.
    /// No FOR UPDATE lock — version-guarded settling makes double-processing safe.
    /// </summary>
    Task<IReadOnlyList<Notification>> ListStuckSendingAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional update that marks the notification
    /// <see cref="NotificationState.FailedTransient"/> for durable retry: sets
    /// <c>NextEligibleAt</c>, increments <c>AttemptCount</c>, and records the
    /// <c>FailureCategory</c>. Returns rows-affected (1 = success, 0 = lost race).
    /// </summary>
    Task<int> MarkTransientRetryAsync(
        string tenantId,
        string notificationId,
        long expectedVersion,
        NotificationState expectedState,
        DateTimeOffset nextEligibleAt,
        string? failureCategory,
        CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant scan for terminal booking operations that have NO notification in
    /// <see cref="NotificationState.Accepted"/> or <see cref="NotificationState.Delivered"/>
    /// (spec 10 §25.4 / FM#12 / Inv-14, T5b).
    ///
    /// Terminal statuses: Booked, Rejected, Expired, Waitlisted, FailedFinal.
    /// Only operations whose <c>UpdatedAt</c> is older than <paramref name="olderThan"/> are
    /// returned — ensures a brief settle window before the alert fires.
    ///
    /// Uses <c>AsNoTracking().IgnoreQueryFilters()</c> on both DbSets for a cross-tenant scan.
    /// Results ordered by <c>UpdatedAt</c> ascending; at most <paramref name="limit"/> rows.
    /// </summary>
    Task<IReadOnlyList<TerminalOperationRef>> ListTerminalOperationsMissingAcceptedNotificationAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> when the given operation already has at least one
    /// notification in <see cref="NotificationState.Accepted"/> or
    /// <see cref="NotificationState.Delivered"/> (defensive re-check, T5b).
    ///
    /// Uses <c>AsNoTracking().IgnoreQueryFilters()</c> — cross-tenant safe.
    /// </summary>
    Task<bool> HasAcceptedNotificationAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
