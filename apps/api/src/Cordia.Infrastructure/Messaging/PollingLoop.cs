using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Shared poll-loop mechanics for the messaging background services (T3/T5/T6).
/// Centralises the tick → error-log → delay → cancellation contract so all three hosted
/// services run one implementation; each service supplies only its per-tick body, cadence,
/// and the enabled/start/stop logging around the loop.
/// </summary>
internal static class PollingLoop
{
    /// <summary>Floor for the inter-tick delay. Guards against a misconfigured interval (0 → a
    /// CPU-spinning loop; negative → <c>Task.Delay</c> throws and crashes the worker).</summary>
    public const int MinDelayMs = 50;

    /// <summary>
    /// Runs <paramref name="tickAsync"/> repeatedly, sleeping <paramref name="pollIntervalMs"/> ms
    /// between ticks, until <paramref name="stoppingToken"/> is cancelled.  An unhandled tick
    /// exception is logged (with <paramref name="serviceName"/>/<paramref name="workerId"/>) and the
    /// loop continues; cancellation breaks the loop cleanly. The delay is clamped to a sane minimum
    /// so an invalid configured interval can neither spin the CPU nor crash the loop.
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> tickAsync,
        int pollIntervalMs,
        ILogger logger,
        string serviceName,
        string workerId,
        CancellationToken stoppingToken)
    {
        var delayMs = Math.Max(MinDelayMs, pollIntervalMs);
        if (delayMs != pollIntervalMs)
        {
            logger.LogWarning(
                "{ServiceName} {WorkerId}: configured poll interval {Configured}ms is below the floor; " +
                "using {Floor}ms", serviceName, workerId, pollIntervalMs, MinDelayMs);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await tickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{ServiceName} {WorkerId}: unhandled tick error", serviceName, workerId);
            }

            try
            {
                await Task.Delay(delayMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
