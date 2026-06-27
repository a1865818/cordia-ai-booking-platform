using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that drains due scheduled jobs on a polling tick (§5.1 mirror, §7, T5).
///
/// Architecture (mirrors OutboxPublisherHostedService):
///   Claim phase — own short transaction (committed); sets Status=Claimed + lease, NEVER Attempt.
///   Publish phase — NO outer transaction; calls IMessageTransport per job.
///   Mark phase — own implicit transaction (ExecuteUpdateAsync); sets Status=Done, clears lease.
///
/// Attempt semantics (§15.2):
///   ClaimDueAsync NEVER increments Attempt. The MessageId is deterministic per attempt-generation:
///     MessageId = $"sched-{job.JobId}-att{job.Attempt}"
///   A worker restart / lease-expiry reclaim of the SAME attempt redelivers the SAME MessageId
///   so the inbox (T4) deduplicates the duplicate delivery (§5.2/§8).
///   Only RescheduleAsync bumps Attempt → new MessageId → inbox treats it as a fresh delivery.
///
/// Crash between publish and mark → job stays Claimed with lease set → after lease expiry
/// another tick reclaims → republishes → inbox dedups (§5.2/§8).
/// </summary>
public sealed class ScheduledJobPollerHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<ScheduledJobPollerHostedService> logger) : BackgroundService
{
    // Stable per-instance identity for lease ownership; unique across restarts (Guid suffix).
    private readonly string _workerId =
        $"sched-poller-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full claim→publish→mark tick.  Returns the number of jobs marked done.
    /// Public so integration tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> PollDueBatchAsync(CancellationToken ct)
    {
        var poller = _options.ScheduledJobPoller;
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddSeconds(poller.LeaseSeconds);

        // ── Claim phase ─────────────────────────────────────────────────────────────
        // Dedicated scope so the claim DbContext is isolated and its transaction is
        // committed before any publish scope is opened (no nested transactions).
        IReadOnlyList<ClaimedJob> claimed;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var repo = claimScope.ServiceProvider.GetRequiredService<IScheduledJobRepository>();
            var jobs = await repo.ClaimDueAsync(poller.BatchSize, _workerId, leaseExpiresAt, now, ct);

            // Detach: capture only the primitive data we need; the claim scope (and its
            // DbContext/connection) is disposed BEFORE we enter the publish phase.
            claimed = jobs.Select(j => new ClaimedJob(
                j.TenantId,
                j.JobId,
                j.JobType,
                j.DestinationQueue,
                j.Attempt,
                j.Payload,
                j.OperationId)).ToList();
        }

        if (claimed.Count == 0)
            return 0;

        logger.LogDebug(
            "Scheduled-job poller {WorkerId}: claimed {Count} job(s)", _workerId, claimed.Count);

        // ── Publish + Mark phase (per job, fresh scope each) ─────────────────────────
        var markedDone = 0;
        foreach (var job in claimed)
        {
            // An unknown destination_queue is a PERMANENT producer defect — it will never resolve,
            // so leaving the job claimed would loop forever (reclaim after every lease expiry).
            // Terminally mark it Dead to stop the loop; it stays visible for ops triage.
            if (!LogicalQueueRegistry.TryGetByWireName(job.DestinationQueue, out var queue))
            {
                await using var deadScope = scopeFactory.CreateAsyncScope();
                var deadRepo = deadScope.ServiceProvider.GetRequiredService<IScheduledJobRepository>();
                await deadRepo.MarkDeadAsync(job.TenantId, job.JobId, ct);
                logger.LogError(
                    "Scheduled-job poller {WorkerId}: unknown destination_queue '{DestinationQueue}' " +
                    "job_id={JobId}; marked Dead (terminal — permanent routing defect)",
                    _workerId, job.DestinationQueue, job.JobId);
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var transport = scope.ServiceProvider.GetRequiredService<IMessageTransport>();
            var markRepo = scope.ServiceProvider.GetRequiredService<IScheduledJobRepository>();

            var envelope = BuildEnvelope(job);

            try
            {
                await transport.PublishAsync(envelope, queue, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave job claimed: lease will expire → reclaim on next tick (§5.2/§8).
                logger.LogError(
                    ex,
                    "Scheduled-job poller {WorkerId}: transport.PublishAsync failed for " +
                    "job_id={JobId} job_type={JobType}; leaving claimed for lease-expiry reclaim",
                    _workerId, job.JobId, job.JobType);
                continue;
            }

            // Mark phase: independent ExecuteUpdate; never nested inside the claim transaction.
            try
            {
                await markRepo.MarkDoneAsync(job.TenantId, job.JobId, ct);
                markedDone++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Publish succeeded but mark failed → job will be republished after lease
                // expiry → inbox dedups the duplicate (§5.2). Log and continue.
                logger.LogError(
                    ex,
                    "Scheduled-job poller {WorkerId}: MarkDoneAsync failed for " +
                    "job_id={JobId}; job will be republished after lease expiry",
                    _workerId, job.JobId);
            }
        }

        return markedDone;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ScheduledJobPoller.Enabled)
        {
            logger.LogInformation(
                "Scheduled-job poller disabled (Messaging:ScheduledJobPoller:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Scheduled-job poller {WorkerId} starting (PollIntervalMs={PollIntervalMs}, " +
            "BatchSize={BatchSize}, LeaseSeconds={LeaseSeconds})",
            _workerId,
            _options.ScheduledJobPoller.PollIntervalMs,
            _options.ScheduledJobPoller.BatchSize,
            _options.ScheduledJobPoller.LeaseSeconds);

        await PollingLoop.RunAsync(
            PollDueBatchAsync,
            _options.ScheduledJobPoller.PollIntervalMs,
            logger,
            "Scheduled-job poller",
            _workerId,
            stoppingToken);

        logger.LogInformation("Scheduled-job poller {WorkerId} stopped", _workerId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static JobEnvelope BuildEnvelope(ClaimedJob job)
    {
        // MessageId is deterministic per attempt-generation (§15.2, §5.2):
        //   "sched-{jobId}-att{attempt}"
        // A reclaim of the SAME attempt (worker crash / lease expiry) redelivers the SAME
        // MessageId → inbox (T4) deduplicates the duplicate delivery.
        // A genuine retry via RescheduleAsync bumps Attempt → new MessageId → fresh delivery.
        var messageId = $"sched-{job.JobId}-att{job.Attempt}";

        // Parse payload; fall back to empty object on malformed JSON (avoids infinite retry on
        // unparse-able rows).
        var payloadElement = JobPayload.ParseOrEmpty(job.Payload);

        return new JobEnvelope(
            MessageId: messageId,
            MessageType: job.JobType,
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: job.TenantId,
            // CorrelationId must be non-null; prefer OperationId, fall back to JobId.
            CorrelationId: job.OperationId ?? job.JobId,
            CausationId: null,
            OperationId: job.OperationId,
            IntentId: null,
            IntentRevision: null,
            AttemptHint: job.Attempt,
            Payload: payloadElement);
    }

    /// <summary>
    /// Immutable snapshot of a claimed scheduled job, detached from the EF change-tracker.
    /// Passed from the claim scope to the publish/mark scope.
    /// </summary>
    private sealed record ClaimedJob(
        string TenantId,
        string JobId,
        string JobType,
        string DestinationQueue,
        int Attempt,
        string Payload,
        string? OperationId);
}
