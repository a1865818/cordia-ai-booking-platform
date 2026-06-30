using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that periodically refreshes the observable gauge cached values in
/// <see cref="Metrics"/> (spec 13 §25.2, T5). The gauge callbacks themselves do no DB I/O —
/// they just return the pre-fetched values.
///
/// Fail-open: exceptions in the refresh loop are caught and logged; the service never crashes.
/// Disabled in test environments via <c>Messaging:MetricsGauge:Enabled=false</c>.
/// </summary>
public sealed class MetricsGaugeHostedService(
    IServiceScopeFactory scopeFactory,
    Metrics metrics,
    IOptions<MessagingOptions> options,
    ILogger<MetricsGaugeHostedService> logger) : BackgroundService
{
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MetricsGauge.Enabled)
        {
            logger.LogInformation(
                "Metrics gauge service disabled (Messaging:MetricsGauge:Enabled=false); will not run.");
            return;
        }

        var intervalMs = Math.Max(1000, _options.MetricsGauge.IntervalSeconds * 1000);

        logger.LogInformation(
            "Metrics gauge service starting (IntervalSeconds={IntervalSeconds}).",
            _options.MetricsGauge.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fail-open: log and continue — a gauge blip must never crash the service.
                logger.LogError(ex, "Metrics gauge service: error refreshing gauges; will retry on next tick.");
            }

            await Task.Delay(intervalMs, stoppingToken).ConfigureAwait(false);
        }

        logger.LogInformation("Metrics gauge service stopped.");
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dlqRepo = scope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
        var workItemRepo = scope.ServiceProvider.GetRequiredService<IOperationWorkItemRepository>();

        metrics.DlqDepth = await dlqRepo.CountQuarantinedAsync(ct);
        metrics.DlqOldestAgeSeconds = await dlqRepo.OldestQuarantinedAgeSecondsAsync(ct);
        metrics.ManualReviewDepth = await workItemRepo.CountOpenAsync(ct);
        metrics.ManualReviewOldestAgeSeconds = await workItemRepo.OldestOpenAgeSecondsAsync(ct);
    }
}
