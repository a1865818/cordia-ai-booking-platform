using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Background service that sweeps for terminal booking operations missing an
/// accepted/delivered notification (spec 10 §25.4 / FM#12 / Inv-14, T5b).
///
/// Per tick, lists terminal operations older than <c>TerminalAgeSeconds</c> that have NO
/// notification in <c>Accepted</c> or <c>Delivered</c> state, then per-operation: opens a
/// fresh scope, sets the tenant context, and calls
/// <see cref="IMissingNotificationAuditor.AuditAsync"/> (implemented in Application via
/// MediatR — resolved through the domain interface so Infrastructure does not reference
/// Application directly).
///
/// Covers BOTH the never-sent case (notification row never created) AND the retry-exhausted
/// <c>FailedPermanent</c> case — both result in no <c>Accepted</c>/<c>Delivered</c> row.
///
/// Inv-14: never mutates <c>booking_operations</c>; the booking stays confirmed.
/// Disabled via <c>Messaging:MissingNotificationAudit:Enabled=false</c> in appsettings.Test.json.
/// </summary>
public sealed class MissingNotificationAuditHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<MissingNotificationAuditHostedService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"missing-notif-auditor-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>
    /// Executes one full audit tick. Returns the number of operations for which a work item
    /// was created (or attempted). Public so integration tests can invoke a single tick
    /// without running the full loop.
    /// </summary>
    public async Task<int> AuditOnceAsync(CancellationToken ct)
    {
        var audit = _options.MissingNotificationAudit;
        var olderThan = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(audit.TerminalAgeSeconds);

        // ── List phase ─────────────────────────────────────────────────────────────────
        // Single scope for the list query; disposed before opening per-op scopes.
        IReadOnlyList<Domain.Notifications.TerminalOperationRef> refs;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var repo = listScope.ServiceProvider.GetRequiredService<INotificationRepository>();
            refs = await repo.ListTerminalOperationsMissingAcceptedNotificationAsync(
                olderThan, audit.BatchSize, ct);
        }

        if (refs.Count == 0)
            return 0;

        logger.LogDebug(
            "MissingNotificationAudit {WorkerId}: found {Count} terminal op(s) missing a notification",
            _workerId, refs.Count);

        // ── Audit phase (per op, fresh scope each) ────────────────────────────────────
        var audited = 0;
        foreach (var opRef in refs)
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Set tenant context so the tenant-filtered DbContext sees this tenant's rows.
            // Background scopes have no HTTP middleware, so TenantContext.TenantId is empty
            // unless set here — mirroring SlaEvaluatorHostedService / ReconcileNotificationsHostedService.
            var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantContext.TenantId = opRef.TenantId;

            var auditor = scope.ServiceProvider.GetRequiredService<IMissingNotificationAuditor>();

            try
            {
                await auditor.AuditAsync(opRef.TenantId, opRef.OperationId, ct);
                audited++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "MissingNotificationAudit {WorkerId}: AuditAsync failed for " +
                    "operation_id={OperationId}; will retry on next tick",
                    _workerId, opRef.OperationId);
            }
        }

        return audited;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MissingNotificationAudit.Enabled)
        {
            logger.LogInformation(
                "MissingNotificationAudit disabled (Messaging:MissingNotificationAudit:Enabled=false); " +
                "worker {WorkerId} will not run", _workerId);
            return;
        }

        logger.LogInformation(
            "MissingNotificationAudit {WorkerId} starting " +
            "(PollIntervalMs={PollIntervalMs}, BatchSize={BatchSize}, TerminalAgeSeconds={TerminalAgeSeconds})",
            _workerId,
            _options.MissingNotificationAudit.PollIntervalMs,
            _options.MissingNotificationAudit.BatchSize,
            _options.MissingNotificationAudit.TerminalAgeSeconds);

        await PollingLoop.RunAsync(
            AuditOnceAsync,
            _options.MissingNotificationAudit.PollIntervalMs,
            logger,
            "MissingNotificationAudit",
            _workerId,
            stoppingToken);

        logger.LogInformation("MissingNotificationAudit {WorkerId} stopped", _workerId);
    }
}
