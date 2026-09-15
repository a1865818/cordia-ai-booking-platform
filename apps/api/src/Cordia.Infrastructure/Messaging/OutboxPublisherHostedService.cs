using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that drains the transactional outbox on a polling tick (§5.1, §7, T3).
///
/// Architecture (§5.1 claim→publish→mark, no nested transactions):
///   Claim phase — own short transaction (committed); sets lease, increments publish_attempts.
///   Publish phase — NO outer transaction; calls IMessageTransport per message.
///   Mark phase — own implicit transaction (ExecuteUpdateAsync); sets published_at, clears lease.
///
/// A fresh DI scope is opened per message in the publish+mark phase so the (future) inbox
/// transaction and the mark transaction are never nested inside the claim transaction (§5.1).
///
/// Crash between publish and mark → row stays published_at IS NULL with lease set → after
/// lease expiry another tick reclaims → republishes → inbox dedups (§5.2/§8).
/// </summary>
public sealed class OutboxPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<OutboxPublisherHostedService> logger) : BackgroundService
{
    // Synthetic consumer_name for publisher-side dead-letter rows (no real consumer was reached).
    private const string PublisherConsumerName = "outbox.publisher";

    // Stable per-instance identity for lease ownership; unique across restarts (Guid suffix).
    private readonly string _workerId =
        $"outbox-pub-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full claim→publish→mark tick.  Returns the number of messages published.
    /// Public so integration tests can invoke a single tick without running the full loop.
    /// </summary>
    public async Task<int> PublishPendingBatchAsync(CancellationToken ct)
    {
        var pub = _options.Publisher;
        var now = DateTimeOffset.UtcNow;
        // The claim stamps one lease on the whole batch. PublishLaneAsync renews it before every
        // transport call (#102), so a row waiting behind slow rows in its lane never outlives it.
        var leaseExpiresAt = now.AddSeconds(pub.LeaseSeconds);

        // ── Claim phase ─────────────────────────────────────────────────────────────
        // Open a dedicated scope so the claim DbContext is isolated and its transaction
        // is committed before any publish scope is opened.
        IReadOnlyList<ClaimedRow> claimed;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var repo = claimScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var rows = await repo.ClaimBatchAsync(pub.BatchSize, _workerId, leaseExpiresAt, now, ct);

            // Detach: capture only the primitive data we need; the claim scope (and its
            // DbContext/connection) is disposed BEFORE we enter the publish phase — this
            // is what makes the architecture constraint hold (no nested transactions).
            claimed = rows.Select(r => new ClaimedRow(
                r.TenantId,
                r.MessageId,
                r.MessageType,
                r.OccurredAt,
                r.CorrelationId,
                r.CausationId,
                r.OperationId,
                r.Payload,
                r.PublishAttempts)).ToList();
        }

        if (claimed.Count == 0)
            return 0;

        logger.LogDebug(
            "Outbox publisher {WorkerId}: claimed {Count} row(s)", _workerId, claimed.Count);

        var maxAttempts = pub.MaxPublishAttempts;

        // ── Route phase ─────────────────────────────────────────────────────────────
        // Resolve every claimed row to its logical queue BEFORE dispatching any of them, so the
        // publish phase can run one lane per queue.
        var lanes = new Dictionary<LogicalQueue, List<ClaimedRow>>();
        var unroutable = new List<ClaimedRow>();

        foreach (var row in claimed)
        {
            if (LogicalQueueRegistry.TryResolve(row.MessageType, out var resolved))
            {
                if (!lanes.TryGetValue(resolved, out var lane))
                    lanes[resolved] = lane = [];

                // Insertion order preserves the claim's ORDER BY occurred_at within the lane.
                lane.Add(row);
            }
            else
            {
                unroutable.Add(row);
            }
        }

        // An unroutable message_type is a PERMANENT producer defect — it will never route, so
        // leaving it unpublished would reclaim it forever. Dead-letter it (so it stops looping
        // and stays visible/replayable) instead of waiting out the attempt cap. Done before the
        // lanes start: it touches no transport and must not be delayed behind a slow lane.
        foreach (var row in unroutable)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await DeadLetterPoisonAsync(scope, row, "unroutable_message_type", ct);
            logger.LogError(
                "Outbox publisher {WorkerId}: unroutable message_type {MessageType} " +
                "message_id={MessageId}; dead-lettered (permanent producer defect)",
                _workerId, row.MessageType, row.MessageId);
        }

        // ── Publish + Mark phase ────────────────────────────────────────────────────
        // Lanes run CONCURRENTLY with each other; rows WITHIN a lane stay strictly sequential.
        //
        // Both halves matter. Sequential-within-lane preserves the per-queue ordering the registry
        // declares (LogicalQueueMetadata.OrderingRequired is true for booking.commands and
        // booking.reconcile). Concurrent-across-lanes is the fix for #96: menu.commands carries a
        // menu extraction that can hold for LangGraphOptions.ExtractTimeoutSeconds (120 s) inside
        // the consumer's inbox transaction, and before this every booking.commands and
        // notification.commands row claimed in the same batch waited behind it — head-of-line
        // blocking across unrelated queues, which spec 05 never budgets for.
        //
        // Concurrency is bounded by the number of distinct logical queues in the batch (≤ 9), not
        // by BatchSize, so this cannot open an unbounded number of scopes/connections.
        var laneCounts = await Task.WhenAll(
            lanes.Select(lane => PublishLaneAsync(lane.Key, lane.Value, maxAttempts, pub.LeaseSeconds, ct)));

        return laneCounts.Sum();
    }

    /// <summary>
    /// Publishes one logical queue's claimed rows, strictly in order, each in its own scope.
    /// This is the original per-row body; only the scheduling around it changed (#96).
    /// </summary>
    private async Task<int> PublishLaneAsync(
        LogicalQueue queue,
        IReadOnlyList<ClaimedRow> rows,
        int maxAttempts,
        int leaseSeconds,
        CancellationToken ct)
    {
        var published = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            await using var scope = scopeFactory.CreateAsyncScope();

            var transport = scope.ServiceProvider.GetRequiredService<IMessageTransport>();
            var markRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

            // ── Lease renewal (#102, spec 05 OD-3) ──────────────────────────────────────
            // The claim's lease is sized for ONE slow row (LeaseSeconds ≈ 2.5× an extraction),
            // but rows in a lane publish one after another, so the third 120 s row used to start
            // after its lease had already expired. Another publisher would reclaim it, burn a
            // publish_attempts increment on a healthy row, and block on the inbox lock.
            //
            // Renewing every row still waiting in this lane, immediately before each transport
            // call, keeps the budget per-row: whatever is running now has LeaseSeconds of cover,
            // and so does everything queued behind it.
            //
            // The current row is renewed on its own so its answer is unambiguous. Zero means a
            // publisher reclaimed it while it waited — that worker owns it now, so this one steps
            // aside instead of publishing it a second time.
            var renewedUntil = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
            if (await markRepo.RenewLeaseAsync([row.MessageId], _workerId, renewedUntil, ct) == 0)
            {
                logger.LogWarning(
                    "Outbox publisher {WorkerId}: lease on message_id={MessageId} was lost before publish "
                    + "(reclaimed or already published); skipping it",
                    _workerId, row.MessageId);
                continue;
            }

            if (i + 1 < rows.Count)
            {
                await markRepo.RenewLeaseAsync(
                    rows.Skip(i + 1).Select(r => r.MessageId).ToList(), _workerId, renewedUntil, ct);
            }

            var envelope = BuildEnvelope(row);

            try
            {
                await transport.PublishAsync(envelope, queue, ct);
            }
            catch (UnconsumedMessageTypeException pex)
            {
                // A routable type with no registered consumer (and no intentionally-unconsumed
                // declaration) is a PERMANENT registration defect — retrying cannot fix it.
                // Dead-letter immediately, mirroring the unroutable_message_type path above.
                // (A consumer-thrown PoisonMessageException deliberately falls to the generic
                // catch below: retry, then publish_attempts_exhausted.)
                await DeadLetterPoisonAsync(scope, row, "unconsumed_message_type", ct);
                logger.LogError(
                    pex,
                    "Outbox publisher {WorkerId}: message_type {MessageType} has no registered consumer " +
                    "message_id={MessageId}; dead-lettered (registration defect)",
                    _workerId, row.MessageType, row.MessageId);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // PublishAttempts is incremented on every claim. Past the cap, treat the row as
                // poison and dead-letter it (stop the unbounded reclaim/error loop); otherwise leave
                // it unpublished so the lease expires and a later tick reclaims it (§5.2/§8).
                if (row.PublishAttempts >= maxAttempts)
                {
                    await DeadLetterPoisonAsync(scope, row, "publish_attempts_exhausted", ct);
                    logger.LogError(
                        ex,
                        "Outbox publisher {WorkerId}: message_id={MessageId} message_type={MessageType} " +
                        "exhausted {MaxAttempts} publish attempts; dead-lettered",
                        _workerId, row.MessageId, row.MessageType, maxAttempts);
                }
                else
                {
                    logger.LogError(
                        ex,
                        "Outbox publisher {WorkerId}: transport.PublishAsync failed for " +
                        "message_id={MessageId} message_type={MessageType} (attempt {Attempt}/{MaxAttempts}); " +
                        "leaving unpublished for lease-expiry reclaim",
                        _workerId, row.MessageId, row.MessageType, row.PublishAttempts, maxAttempts);
                }
                continue;
            }

            // Mark phase: independent transaction; never nested inside the claim transaction.
            try
            {
                await markRepo.MarkPublishedAsync(row.TenantId, row.MessageId, DateTimeOffset.UtcNow, ct);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Publish succeeded but mark failed → row will be republished after lease
                // expiry → inbox dedups the duplicate (§5.2). Log and continue.
                logger.LogError(
                    ex,
                    "Outbox publisher {WorkerId}: MarkPublishedAsync failed for " +
                    "message_id={MessageId}; row will be republished after lease expiry",
                    _workerId, row.MessageId);
            }
        }

        return published;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Publisher.Enabled)
        {
            logger.LogInformation(
                "Outbox publisher disabled (Messaging:Publisher:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "Outbox publisher {WorkerId} starting (PollIntervalMs={PollIntervalMs}, " +
            "BatchSize={BatchSize}, LeaseSeconds={LeaseSeconds})",
            _workerId,
            _options.Publisher.PollIntervalMs,
            _options.Publisher.BatchSize,
            _options.Publisher.LeaseSeconds);

        await PollingLoop.RunAsync(
            PublishPendingBatchAsync,
            _options.Publisher.PollIntervalMs,
            logger,
            "Outbox publisher",
            _workerId,
            stoppingToken);

        logger.LogInformation("Outbox publisher {WorkerId} stopped", _workerId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quarantines a poison outbox row and marks it published in ONE transaction (so the two are
    /// atomic — a crash leaves the row reclaimable with no orphan DLQ row, and a successful run stops
    /// the reclaim loop). The DLQ row uses the synthetic consumer_name "outbox.publisher" since no
    /// real consumer was reached; it can later be replayed (never-admitted → original message_id).
    /// </summary>
    private static async Task DeadLetterPoisonAsync(
        AsyncServiceScope scope, ClaimedRow row, string errorClass, CancellationToken ct)
    {
        var dlqRepo = scope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
        var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await dlqRepo.AddAsync(new DeadLetterMessage
            {
                TenantId = row.TenantId,
                MessageId = row.MessageId,
                ConsumerName = PublisherConsumerName,
                MessageType = row.MessageType,
                Payload = row.Payload,
                ErrorClass = errorClass,
                AttemptHistory = "[]",
                FirstFailedAt = row.OccurredAt,
                DeadLetteredAt = DateTimeOffset.UtcNow,
                ReplayState = DlqReplayState.Quarantined,
            }, ct2);

            // Mark the outbox row published so it is no longer claimed — its durable record now lives
            // in dead_letter_messages.
            await outboxRepo.MarkPublishedAsync(row.TenantId, row.MessageId, DateTimeOffset.UtcNow, ct2);
        }, ct);
    }

    private static JobEnvelope BuildEnvelope(ClaimedRow row)
    {
        // Parse payload once; extract intent_id / intent_revision if present (§4.1).
        var payload = JobPayload.ParseOrEmpty(row.Payload);

        return new JobEnvelope(
            MessageId: row.MessageId,
            MessageType: row.MessageType,
            OccurredAt: row.OccurredAt,
            TenantId: row.TenantId,
            CorrelationId: row.CorrelationId,
            CausationId: row.CausationId,
            OperationId: row.OperationId,
            IntentId: payload.GetStringOrNull("intent_id"),
            IntentRevision: payload.GetInt32OrNull("intent_revision"),
            AttemptHint: row.PublishAttempts,
            Payload: payload);
    }

    /// <summary>
    /// Immutable snapshot of a claimed outbox row, detached from the EF change-tracker.
    /// Passed from the claim scope to the publish/mark scope.
    /// </summary>
    private sealed record ClaimedRow(
        string TenantId,
        string MessageId,
        string MessageType,
        DateTimeOffset OccurredAt,
        string CorrelationId,
        string? CausationId,
        string? OperationId,
        string Payload,
        int PublishAttempts);
}
