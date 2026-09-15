using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that reconciles notifications stuck in <c>Sending</c>
/// (spec 10 §12.8 / §13.6 capability-ladder recovery, T4).
///
/// Per tick, scans for stuck-Sending rows older than the configured threshold, then per-row:
/// opens a fresh scope, sets the tenant context, and calls
/// <see cref="INotificationReconciler.ReconcileAsync"/> (implemented in Application via MediatR —
/// resolved through the domain interface so Infrastructure does not reference Application directly).
///
/// Disabled via <c>Messaging:NotificationReconcile:Enabled=false</c> in appsettings.Test.json.
/// </summary>
public sealed class ReconcileNotificationsHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<ReconcileNotificationsHostedService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"notification-reconciler-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full reconcile tick. Returns the number of notifications reconciled.
    /// Public so integration tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> ReconcileOnceAsync(CancellationToken ct)
    {
        var reconcile = _options.NotificationReconcile;
        var now = DateTimeOffset.UtcNow;
        var stuckThreshold = now - TimeSpan.FromSeconds(reconcile.StuckThresholdSeconds);

        // ── List phase ─────────────────────────────────────────────────────────────────
        // Single scope for the list query; disposed before opening per-row scopes.
        IReadOnlyList<NotifSnapshot> snapshots;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var repo = listScope.ServiceProvider.GetRequiredService<INotificationRepository>();
            var rows = await repo.ListStuckSendingAsync(stuckThreshold, reconcile.BatchSize, ct);

            // Detach: capture only primitive data; the list scope is disposed before
            // the per-row reconcile phase (no nested scopes / connections held across reconciles).
            snapshots = rows.Select(n => new NotifSnapshot(n.TenantId, n.NotificationId)).ToList();
        }

        if (snapshots.Count == 0)
            return 0;

        logger.LogDebug(
            "Notification reconciler {WorkerId}: found {Count} stuck-Sending row(s) to reconcile",
            _workerId, snapshots.Count);

        // ── Reconcile phase (per row, fresh scope each) ───────────────────────────────
        var reconciled = 0;
        foreach (var snap in snapshots)
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Set tenant context so the tenant-filtered DbContext sees this tenant's rows.
            // Background scopes have no HTTP middleware, so TenantContext.TenantId is empty
            // unless set here — mirroring what SlaEvaluatorHostedService does per operation.
            var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantContext.SetTenant(snap.TenantId);

            var reconciler = scope.ServiceProvider.GetRequiredService<INotificationReconciler>();

            try
            {
                await reconciler.ReconcileAsync(snap.TenantId, snap.NotificationId, ct);
                reconciled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Notification reconciler {WorkerId}: ReconcileAsync failed for " +
                    "notification_id={NotificationId}; will retry on next tick",
                    _workerId, snap.NotificationId);
            }
        }

        return reconciled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.NotificationReconcile.Enabled)
        {
            logger.LogInformation(
                "Notification reconciler disabled (Messaging:NotificationReconcile:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Notification reconciler {WorkerId} starting " +
            "(PollIntervalMs={PollIntervalMs}, BatchSize={BatchSize}, StuckThresholdSeconds={StuckThresholdSeconds})",
            _workerId,
            _options.NotificationReconcile.PollIntervalMs,
            _options.NotificationReconcile.BatchSize,
            _options.NotificationReconcile.StuckThresholdSeconds);

        await PollingLoop.RunAsync(
            ReconcileOnceAsync,
            _options.NotificationReconcile.PollIntervalMs,
            logger,
            "Notification reconciler",
            _workerId,
            stoppingToken);

        logger.LogInformation("Notification reconciler {WorkerId} stopped", _workerId);
    }

    private sealed record NotifSnapshot(string TenantId, string NotificationId);
}
