using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that finds stale non-terminal workflow steps and re-enqueues
/// a continuation message so admitted-but-incomplete external effects are never forgotten
/// (§3, §5.1, §6, §7, §11, T6).
///
/// Design (STATELESS, IDEMPOTENT re-enqueue — no claim/lease):
///   The sweeper is a safety net, not the primary path (§7).  It does NOT mutate the
///   workflow step; step transitions are owned by spec-06 workers.
///
///   Idempotency comes from a VERSION-STAMPED MessageId (§6, §12.4):
///     MessageId = $"sweep-{stepId}-v{version}"
///   Two sweepers re-enqueueing the same unchanged step produce the SAME MessageId →
///   the inbox (T4) deduplicates them → continuation runs once.
///   When the step advances (Version changes) and stalls again it gets a NEW MessageId →
///   recovery is never permanently suppressed (§6 / §12.4).
///
/// Crash-safety: a crash mid-sweep means some steps are not re-enqueued in that tick;
/// the next tick will find them again (staleness threshold still exceeded).  No lease is
/// needed because re-enqueuing the same step twice is harmless (inbox dedups).
/// </summary>
public sealed class StepSweeperHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<StepSweeperHostedService> logger) : BackgroundService
{
    // Stable per-instance identity for log correlation; unique across restarts (Guid suffix).
    private readonly string _workerId =
        $"step-sweeper-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full sweep tick.  Returns the number of continuation envelopes published.
    /// Public so integration tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var sweeper = _options.StepSweeper;
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddSeconds(-sweeper.StalenessSeconds);

        // ── List phase ─────────────────────────────────────────────────────────────────
        // Single scope for the list query; disposed before opening per-step publish scopes
        // (no nested scopes / connections held across publishes).
        IReadOnlyList<StepSnapshot> snapshots;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var repo = listScope.ServiceProvider.GetRequiredService<IWorkflowStepRepository>();
            var steps = await repo.ListNonTerminalAsync(staleBefore, now, sweeper.BatchSize, ct);

            // Detach: capture only the primitive data we need; the list scope is disposed
            // BEFORE we enter the per-step publish phase.
            snapshots = steps.Select(s => new StepSnapshot(
                s.TenantId,
                s.WorkflowStepId,
                s.OperationId,
                s.StepType,
                s.AttemptCount,
                s.Version)).ToList();
        }

        if (snapshots.Count == 0)
            return 0;

        logger.LogDebug(
            "Step sweeper {WorkerId}: found {Count} stale non-terminal step(s)", _workerId, snapshots.Count);

        // ── Publish phase (per step, fresh scope each) ────────────────────────────────
        var published = 0;
        foreach (var snap in snapshots)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var transport = scope.ServiceProvider.GetRequiredService<IMessageTransport>();

            var envelope = BuildContinuationEnvelope(snap, now);

            // "booking.step.continue.v1" starts with "booking." → routes to BookingCommands.
            var queue = LogicalQueueRegistry.Resolve(envelope.MessageType);

            try
            {
                await transport.PublishAsync(envelope, queue, ct);
                published++;
            }
            catch (UnconsumedMessageTypeException pex)
            {
                // The sweeper only publishes the hardcoded booking.step.continue.v1 — a missing
                // consumer means the CORE booking-driver registration is gone (a startup/wiring
                // defect retries can never fix). There is no per-step dead state to mark, so
                // surface it at Critical and stop this sweep instead of silently retry-looping.
                logger.LogCritical(
                    pex,
                    "Step sweeper {WorkerId}: no registered consumer for {MessageType} — the " +
                    "booking driver registration is missing; aborting this sweep",
                    _workerId, envelope.MessageType);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log and continue: the next sweep will retry (the step is still stale).
                // This is a safety net — transient transport failures are not fatal.
                logger.LogError(
                    ex,
                    "Step sweeper {WorkerId}: transport.PublishAsync failed for " +
                    "workflow_step_id={WorkflowStepId} version={Version}; will retry on next sweep",
                    _workerId, snap.WorkflowStepId, snap.Version);
            }
        }

        return published;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.StepSweeper.Enabled)
        {
            logger.LogInformation(
                "Step sweeper disabled (Messaging:StepSweeper:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Step sweeper {WorkerId} starting (IntervalMs={IntervalMs}, " +
            "BatchSize={BatchSize}, StalenessSeconds={StalenessSeconds})",
            _workerId,
            _options.StepSweeper.IntervalMs,
            _options.StepSweeper.BatchSize,
            _options.StepSweeper.StalenessSeconds);

        await PollingLoop.RunAsync(
            SweepOnceAsync,
            _options.StepSweeper.IntervalMs,
            logger,
            "Step sweeper",
            _workerId,
            stoppingToken);

        logger.LogInformation("Step sweeper {WorkerId} stopped", _workerId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static JobEnvelope BuildContinuationEnvelope(StepSnapshot snap, DateTimeOffset now)
    {
        // Version-stamped MessageId (§6 / §12.4 "duplicate-suppression never suppresses recovery"):
        //   $"sweep-{stepId}-v{version}"
        // Two sweepers processing the SAME unchanged step (same Version) produce the SAME
        // MessageId → the inbox (T4) deduplicates them → continuation runs exactly once.
        // When the step advances (Version increments) and stalls again, the new MessageId
        // is distinct from all prior sweep messages → recovery is NEVER permanently suppressed.
        var messageId = $"sweep-{snap.WorkflowStepId}-v{snap.Version}";

        // Payload carries reference-by-ID (§12.2 — no secrets, no large payload).
        var payload = JsonSerializer.SerializeToElement(new
        {
            workflow_step_id = snap.WorkflowStepId,
            operation_id = snap.OperationId,
            step_type = snap.StepType,
        });

        return new JobEnvelope(
            MessageId: messageId,
            MessageType: "booking.step.continue.v1",
            OccurredAt: now,
            TenantId: snap.TenantId,
            CorrelationId: snap.OperationId,   // non-null: OperationId is always set on a step
            CausationId: null,
            OperationId: snap.OperationId,
            IntentId: null,
            IntentRevision: null,
            AttemptHint: snap.AttemptCount,
            Payload: payload);
    }

    /// <summary>
    /// Immutable snapshot of a stale workflow step, detached from the EF change-tracker.
    /// Passed from the list scope to the per-step publish scope.
    /// </summary>
    private sealed record StepSnapshot(
        string TenantId,
        string WorkflowStepId,
        string OperationId,
        string StepType,
        int AttemptCount,
        long Version);
}
