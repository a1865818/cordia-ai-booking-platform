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

        // ── Publish + Mark phase (per message, fresh scope each) ────────────────────
        var published = 0;
        foreach (var row in claimed)
        {
            // Route first; skip unroutable types without opening a scope.
            if (!LogicalQueueRegistry.TryResolve(row.MessageType, out var queue))
            {
                logger.LogError(
                    "Outbox publisher {WorkerId}: unroutable message_type {MessageType} " +
                    "message_id={MessageId}; leaving unpublished (producer defect, DLQ in T7)",
                    _workerId, row.MessageType, row.MessageId);
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var transport = scope.ServiceProvider.GetRequiredService<IMessageTransport>();
            var markRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

            var envelope = BuildEnvelope(row);

            try
            {
                await transport.PublishAsync(envelope, queue, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave row unpublished: lease will expire → reclaim on next tick (§5.2/§8).
                logger.LogError(
                    ex,
                    "Outbox publisher {WorkerId}: transport.PublishAsync failed for " +
                    "message_id={MessageId} message_type={MessageType}; " +
                    "leaving unpublished for lease-expiry reclaim",
                    _workerId, row.MessageId, row.MessageType);
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
