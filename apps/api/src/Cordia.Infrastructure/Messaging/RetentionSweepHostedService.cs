using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Schedules the spec-14 privacy retention sweeps (§24.3, spec 12 §6.3) —
/// before this service existed the sweeps were dead code outside of tests.
/// Runs one <see cref="IRetentionSweeper.SweepAllTenantsAsync"/> cycle per
/// configured interval (daily by default; retention windows are measured in
/// days, so cadence precision is irrelevant). Idempotent by construction:
/// every sweep re-derives its candidates from the stored deadlines, so
/// overlapping/missed cycles are harmless.
/// </summary>
public sealed class RetentionSweepHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<RetentionSweepHostedService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"retention-sweep-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full sweep cycle. Public so integration tests can invoke a
    /// single cycle without running the loop (same pattern as the step sweeper).
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<IRetentionSweeper>();
        return await sweeper.SweepAllTenantsAsync(DateTimeOffset.UtcNow, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.RetentionSweep;
        if (!opts.Enabled)
        {
            logger.LogInformation(
                "Retention sweeper disabled (Messaging:RetentionSweep:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Retention sweeper {WorkerId} starting (IntervalHours={IntervalHours}, InitialDelaySeconds={InitialDelay})",
            _workerId, opts.IntervalHours, opts.InitialDelaySeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, opts.InitialDelaySeconds)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var intervalMs = (int)Math.Min(
            int.MaxValue,
            TimeSpan.FromHours(Math.Max(1, opts.IntervalHours)).TotalMilliseconds);

        await PollingLoop.RunAsync(
            async ct =>
            {
                var pruned = await SweepOnceAsync(ct);
                logger.LogInformation(
                    "Retention sweeper {WorkerId}: cycle complete, {Pruned} row(s) pruned/anonymized",
                    _workerId, pruned);
            },
            intervalMs,
            logger,
            "Retention sweeper",
            _workerId,
            stoppingToken);

        logger.LogInformation("Retention sweeper {WorkerId} stopped", _workerId);
    }
}
