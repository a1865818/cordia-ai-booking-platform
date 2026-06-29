using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications;

/// <summary>
/// Scoped helper: decides whether a <c>FailedTransient</c> notification should be retried
/// or permanently failed, and in the retry case enqueues a durable <see cref="ScheduledJob"/>
/// (spec 10 §8, FM#6/FM#7, T5a).
///
/// <para><b>Budget exhausted (FM#7):</b> calls <c>MarkFailedAsync → FailedPermanent</c> with
/// reason <c>"retry_exhausted"</c>. Never touches <c>booking_operations</c> (Inv-8).</para>
///
/// <para><b>Budget remaining (FM#6):</b> calls <c>MarkTransientRetryAsync</c> then inserts
/// a <see cref="ScheduledJob"/> (idempotent: JobId = <c>sj_notifretry_{notificationId}_a{attempt}</c>)
/// with JobType <c>"notification.retry.requested.v1"</c> and DestinationQueue
/// <c>"notification.commands"</c> so <c>ScheduledJobPollerHostedService</c> re-fires the
/// delivery via the <c>RetryNotificationCommandHandler</c>.</para>
///
/// <para>MUST be called inside the ambient inbox/UoW transaction so the job insertion and
/// state update are atomic. Register as scoped.</para>
/// </summary>
public sealed class NotificationRetryScheduler(
    IScheduledJobRepository jobs,
    IRetryPolicy retryPolicy,
    INotificationRepository notifications,
    IAuditEventRepository audit,
    ILogger<NotificationRetryScheduler> logger)
{
    // Per-priority retry budgets aligned with spec §8 SLA windows.
    private static readonly StepRetryBudget CriticalBudget =
        new(6, TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

    private static readonly StepRetryBudget HighBudget =
        new(5, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    private static readonly StepRetryBudget NormalBudget =
        new(3, TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));

    private static StepRetryBudget BudgetFor(MessagePriority priority) => priority switch
    {
        MessagePriority.Critical => CriticalBudget,
        MessagePriority.High => HighBudget,
        _ => NormalBudget,
    };

    /// <summary>
    /// Handles a transient send failure: either schedules a durable retry (FM#6) or exhausts
    /// to <c>FailedPermanent</c> (FM#7). Never writes to <c>booking_operations</c> (Inv-8).
    /// </summary>
    /// <param name="n">The notification row in state <c>Sending</c> (or <c>FailedTransient</c>
    /// on a re-drive) when the failure occurred.</param>
    /// <param name="expectedVersion">The version of <paramref name="n"/> at the time of the
    /// failing send attempt (used for the version-guarded update).</param>
    /// <param name="failureCategory">Normalized failure reason from the adapter.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleTransientAsync(
        Notification n,
        long expectedVersion,
        string? failureCategory,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // completedAttemptNumber is 1-based: AttemptCount has not yet been incremented by
        // the caller, so AttemptCount + 1 = the attempt that just completed.
        var next = retryPolicy.NextEligibleAt(
            BudgetFor(n.Priority),
            completedAttemptNumber: n.AttemptCount + 1,
            firstAttemptAt: n.CreatedAt,
            now);

        if (next is null)
        {
            // FM#7: budget exhausted → FailedPermanent. Booking is NOT touched (Inv-8).
            var exhaustedRows = await notifications.MarkFailedAsync(
                n.TenantId, n.NotificationId,
                expectedVersion,
                NotificationState.Sending,
                NotificationState.FailedPermanent,
                "retry_exhausted",
                ct);

            if (exhaustedRows == 0)
            {
                // Lost race (version/state moved under us, e.g. a concurrent receipt webhook).
                // The FailedPermanent transition did not take effect — do NOT emit a misleading
                // exhaustion audit.
                logger.LogWarning(
                    "HandleTransientAsync: lost race settling Sending→FailedPermanent (retry_exhausted) for {NotificationId}; skipping audit.",
                    n.NotificationId);
                return;
            }

            await audit.AddAsync(new AuditEvent
            {
                TenantId = n.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "notification.retry_exhausted",
                Actor = "system",
                SubjectRef = n.NotificationId,
                CorrelationId = n.NotificationId,
                OperationId = n.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    notification_id = n.NotificationId,
                    attempt_count = n.AttemptCount + 1,
                    priority = n.Priority.ToString(),
                }),
                OccurredAt = now,
            }, ct);

            return;
        }

        // FM#6: schedule durable retry.
        var attempt = n.AttemptCount + 1;

        var settled = await notifications.MarkTransientRetryAsync(
            n.TenantId, n.NotificationId,
            expectedVersion,
            NotificationState.Sending,
            next.Value,
            failureCategory,
            ct);

        if (settled == 0)
        {
            // Lost race settling Sending→FailedTransient (version/state moved under us).
            // Skip the enqueue + audit so we never commit an orphan retry job for a row that
            // is no longer FailedTransient.
            logger.LogWarning(
                "HandleTransientAsync: lost race settling Sending→FailedTransient for {NotificationId}; skipping retry enqueue.",
                n.NotificationId);
            return;
        }

        var jobId = $"sj_notifretry_{n.NotificationId}_a{attempt}";
        if (!await jobs.ExistsAsync(n.TenantId, jobId, ct))
        {
            await jobs.AddAsync(new ScheduledJob
            {
                TenantId = n.TenantId,
                JobId = jobId,
                JobType = "notification.retry.requested.v1",
                RunAfter = next.Value,
                DestinationQueue = "notification.commands",
                Status = ScheduledJobStatus.Pending,
                Attempt = 0,
                OperationId = n.OperationId,
                Payload = JsonSerializer.Serialize(new
                {
                    message_type = "notification.retry.requested.v1",
                    notification_id = n.NotificationId,
                    tenant_id = n.TenantId,
                    operation_id = n.OperationId,
                }),
            }, ct);
        }

        await audit.AddAsync(new AuditEvent
        {
            TenantId = n.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = "notification.retry_scheduled",
            Actor = "system",
            SubjectRef = n.NotificationId,
            CorrelationId = n.NotificationId,
            OperationId = n.OperationId,
            Metadata = JsonSerializer.Serialize(new
            {
                notification_id = n.NotificationId,
                next_eligible_at = next.Value,
                attempt,
            }),
            OccurredAt = now,
        }, ct);
    }
}
