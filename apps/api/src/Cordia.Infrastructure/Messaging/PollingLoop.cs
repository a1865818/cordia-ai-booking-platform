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
    /// <summary>
    /// Runs <paramref name="tickAsync"/> repeatedly, sleeping <paramref name="pollIntervalMs"/> ms
    /// between ticks, until <paramref name="stoppingToken"/> is cancelled.  An unhandled tick
    /// exception is logged (with <paramref name="serviceName"/>/<paramref name="workerId"/>) and the
    /// loop continues; cancellation breaks the loop cleanly.
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> tickAsync,
        int pollIntervalMs,
        ILogger logger,
        string serviceName,
        string workerId,
        CancellationToken stoppingToken)
    {
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
                await Task.Delay(pollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
