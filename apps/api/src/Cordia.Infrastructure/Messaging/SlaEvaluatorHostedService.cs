using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that evaluates SLA thresholds for non-terminal booking operations
/// (spec 06 §8 / §17.5, T9).
///
/// Per tick, scans for non-terminal ops, then per-op: opens a fresh scope, sets the tenant
/// context, and calls <see cref="ISlaEvaluator.EvaluateAsync"/> (implemented in Application
/// via MediatR — resolved through the domain interface so Infrastructure does not reference
/// Application directly).
///
/// Disabled via <c>Messaging:Sla:Enabled=false</c> in appsettings.Test.json.
/// </summary>
public sealed class SlaEvaluatorHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<SlaEvaluatorHostedService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"sla-evaluator-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full evaluation tick. Returns the number of operations evaluated.
    /// Public so integration tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> EvaluateOnceAsync(CancellationToken ct)
    {
        var sla = _options.Sla;
        var now = DateTimeOffset.UtcNow;

        // ── List phase ─────────────────────────────────────────────────────────────────
        // Single scope for the list query; disposed before opening per-op scopes.
        IReadOnlyList<OpSnapshot> snapshots;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var repo = listScope.ServiceProvider.GetRequiredService<IBookingOperationRepository>();
            var ops = await repo.ListNonTerminalForSlaAsync(now, sla.BatchSize, ct);

            // Detach: capture only primitive data; the list scope is disposed before
            // the per-op evaluate phase (no nested scopes / connections held across evaluations).
            snapshots = ops.Select(o => new OpSnapshot(o.TenantId, o.OperationId)).ToList();
        }

        if (snapshots.Count == 0)
            return 0;

        logger.LogDebug(
            "SLA evaluator {WorkerId}: found {Count} non-terminal op(s) to evaluate",
            _workerId, snapshots.Count);

        // ── Evaluate phase (per op, fresh scope each) ─────────────────────────────────
        var evaluated = 0;
        foreach (var snap in snapshots)
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Set tenant context so the tenant-filtered DbContext sees this tenant's rows.
            // Background scopes have no HTTP middleware, so TenantContext.TenantId is empty
            // unless set here — mirroring what PostgresMessageTransport does per envelope.
            var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantContext.TenantId = snap.TenantId;

            var evaluator = scope.ServiceProvider.GetRequiredService<ISlaEvaluator>();

            try
            {
                await evaluator.EvaluateAsync(snap.TenantId, snap.OperationId, ct);
                evaluated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "SLA evaluator {WorkerId}: EvaluateAsync failed for " +
                    "operation_id={OperationId}; will retry on next tick",
                    _workerId, snap.OperationId);
            }
        }

        return evaluated;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Sla.Enabled)
        {
            logger.LogInformation(
                "SLA evaluator disabled (Messaging:Sla:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "SLA evaluator {WorkerId} starting (IntervalMs={IntervalMs}, BatchSize={BatchSize})",
            _workerId, _options.Sla.IntervalMs, _options.Sla.BatchSize);

        await PollingLoop.RunAsync(
            EvaluateOnceAsync,
            _options.Sla.IntervalMs,
            logger,
            "SLA evaluator",
            _workerId,
            stoppingToken);

        logger.LogInformation("SLA evaluator {WorkerId} stopped", _workerId);
    }

    private sealed record OpSnapshot(string TenantId, string OperationId);
}
