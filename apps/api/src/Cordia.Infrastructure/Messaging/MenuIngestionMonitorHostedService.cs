using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Periodically settles vendor catalogs stuck mid-ingestion (docs §12 gap 8).
///
/// <para>Before this, staleness was computed only in the console, so a stuck extraction was
/// invisible unless an operator opened the menu screen — and nobody watches a screen for something
/// that is meant to finish by itself. This makes the same fact observable without a human looking.</para>
///
/// <para><b>It WRITES.</b> The D4 amendment (owner ruling 2026-08-06) added <c>extract_failed</c>
/// to the status vocabulary, and this job is what drives rows into it — a terminal state whose only
/// recovery is a fresh upload. It is therefore not the read-only reporter it began as, and the
/// staleness threshold is a destructive verdict rather than a hint: see
/// <see cref="Cordia.Domain.Entities.VendorCatalog.StatusChangedAt"/> for why the clock it reads
/// has to be the status-entry time and not the row's creation time.</para>
///
/// <para>Still stateless and idempotent, mirroring
/// <see cref="PreorderRecoverySweeperHostedService"/>: no claim and no lease — every write is
/// version-guarded, so a job that finishes first simply wins. Disabled via
/// <c>Messaging:MenuIngestionMonitor:Enabled=false</c> (appsettings.Test.json) so Alba integration
/// tests are unaffected.</para>
/// </summary>
public sealed class MenuIngestionMonitorHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<MenuIngestionMonitorHostedService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"menu-ingestion-monitor-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Runs one tick and returns how many stalled catalogs were reported. Public so integration
    /// tests can invoke a single tick without running the loop.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var stalledBefore = DateTimeOffset.UtcNow
            .AddSeconds(-_options.MenuIngestionMonitor.StalenessSeconds);

        await using var scope = scopeFactory.CreateAsyncScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IMenuIngestionMonitor>();
        // tenantId: null — a background monitor reports across every tenant.
        return await monitor.SettleStalledAsync(stalledBefore, tenantId: null, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MenuIngestionMonitor.Enabled)
        {
            logger.LogInformation(
                "Menu ingestion monitor disabled (Messaging:MenuIngestionMonitor:Enabled=false); "
                + "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Menu ingestion monitor {WorkerId} starting (IntervalMs={IntervalMs}, StalenessSeconds={StalenessSeconds})",
            _workerId, _options.MenuIngestionMonitor.IntervalMs,
            _options.MenuIngestionMonitor.StalenessSeconds);

        await PollingLoop.RunAsync(
            SweepOnceAsync,
            _options.MenuIngestionMonitor.IntervalMs,
            logger,
            "Menu ingestion monitor",
            _workerId,
            stoppingToken);

        logger.LogInformation("Menu ingestion monitor {WorkerId} stopped", _workerId);
    }
}
