using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background backstop that recovers stuck pre-order sagas (spec 11 §5.3, divergence #3). Each tick
/// enqueues a <c>preorder.commands.reconcile.v1</c> for every non-terminal saga untouched for longer
/// than the configured staleness window, which re-drives the saga's CURRENT step (idempotent — a saga
/// that already advanced is a no-op).
///
/// <para>This is the SECOND line of defence behind the per-step reconcile timeouts scheduled on step
/// entry (<see cref="Cordia.Domain.Interfaces.IScheduledJobRepository"/> jobs): the timeout is the
/// primary recovery signal; this sweep catches sagas whose timeout row itself was never written (e.g.
/// a crash before commit). Staleness is therefore set longer than the longest per-step window.</para>
///
/// <para>STATELESS + IDEMPOTENT (no claim/lease), mirroring <see cref="StepSweeperHostedService"/>:
/// the sweep only writes outbox reconcile messages (each auto-committed by the repository); a crash
/// mid-sweep just means the next tick re-finds the still-stale sagas, and a repeat reconcile is
/// harmless. Disabled via <c>Messaging:PreorderRecovery:Enabled=false</c> (appsettings.Test.json) so
/// Alba integration tests are unaffected.</para>
/// </summary>
public sealed class PreorderRecoverySweeperHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<PreorderRecoverySweeperHostedService> logger) : BackgroundService
{
    // Stable per-instance identity for log correlation; unique across restarts (Guid suffix).
    private readonly string _workerId =
        $"preorder-recovery-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one sweep tick and returns the number of stuck sagas swept. Public so integration
    /// tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var staleBefore = DateTimeOffset.UtcNow.AddSeconds(-_options.PreorderRecovery.StalenessSeconds);

        await using var scope = scopeFactory.CreateAsyncScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<IPreorderRecoverySweeper>();
        var swept = await sweeper.SweepAsync(staleBefore, ct);

        if (swept > 0)
            logger.LogInformation(
                "Preorder recovery sweeper {WorkerId}: enqueued reconcile for {Count} stuck saga(s)",
                _workerId, swept);

        return swept;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PreorderRecovery.Enabled)
        {
            logger.LogInformation(
                "Preorder recovery sweeper disabled (Messaging:PreorderRecovery:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Preorder recovery sweeper {WorkerId} starting (IntervalMs={IntervalMs}, StalenessSeconds={StalenessSeconds})",
            _workerId, _options.PreorderRecovery.IntervalMs, _options.PreorderRecovery.StalenessSeconds);

        await PollingLoop.RunAsync(
            SweepOnceAsync,
            _options.PreorderRecovery.IntervalMs,
            logger,
            "Preorder recovery sweeper",
            _workerId,
            stoppingToken);

        logger.LogInformation("Preorder recovery sweeper {WorkerId} stopped", _workerId);
    }
}
