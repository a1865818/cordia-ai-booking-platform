using Cordia.Application.Handlers.Notifications;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationRetryScheduler"/> (spec 10 FM#6/FM#7, T5a).
/// </summary>
[Trait("Category", "Unit")]
public class NotificationRetrySchedulerTests
{
    private const string TenantId = "tenant-retry-sched";
    private const string NotificationId = "notif-retry-sched-1";
    private const string OperationId = "op-retry-sched-1";

    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IAuditEventRepository> _audit = new();

    private NotificationRetryScheduler BuildScheduler() =>
        new(_jobs.Object, _retryPolicy.Object, _notifications.Object, _audit.Object,
            NullLogger<NotificationRetryScheduler>.Instance);

    private static Notification MakeSendingNotification(
        MessagePriority priority = MessagePriority.High,
        int attemptCount = 0) => new()
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            OperationId = OperationId,
            State = NotificationState.Sending,
            Channel = NotificationChannel.Sms,
            Priority = priority,
            AttemptCount = attemptCount,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1,
            ProviderIdempotencyKey = "pkey-retry-sched-1",
            RecipientRef = "recip-retry-sched",
            TemplateVersion = "v1",
        };

    // ── 1. Budget remaining → MarkTransientRetryAsync + scheduled_job enqueued ──────────────────

    [Fact]
    public async Task TransientWithBudget_MarksRetry_EnqueuesJob_AuditsScheduled()
    {
        var n = MakeSendingNotification();
        var nextTime = DateTimeOffset.UtcNow.AddSeconds(10);

        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);

        _notifications.Setup(r => r.MarkTransientRetryAsync(
                TenantId, NotificationId, 1L, NotificationState.Sending,
                nextTime, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _jobs.Setup(j => j.ExistsAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await BuildScheduler().HandleTransientAsync(n, expectedVersion: 1, "provider_timeout", CancellationToken.None);

        // MarkTransientRetryAsync called
        _notifications.Verify(r => r.MarkTransientRetryAsync(
            TenantId, NotificationId, 1L, NotificationState.Sending,
            nextTime, "provider_timeout", It.IsAny<CancellationToken>()), Times.Once);

        // MarkFailedAsync NOT called (not exhausted)
        _notifications.Verify(r => r.MarkFailedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // scheduled_job added with correct JobType + DestinationQueue
        _jobs.Verify(j => j.AddAsync(
            It.Is<ScheduledJob>(job =>
                job.TenantId == TenantId &&
                job.JobType == "notification.retry.requested.v1" &&
                job.DestinationQueue == "notification.commands" &&
                job.RunAfter == nextTime),
            It.IsAny<CancellationToken>()), Times.Once);

        // audit notification.retry_scheduled emitted
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_scheduled"),
            It.IsAny<CancellationToken>()), Times.Once);

        // No exhausted audit
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_exhausted"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 2. Budget exhausted → MarkFailedAsync(FailedPermanent) + audit; no job, no booking write ─

    [Fact]
    public async Task BudgetExhausted_MarksPermanentFailed_AuditsExhausted_NoJobEnqueued_NoBookingWrite()
    {
        var n = MakeSendingNotification(attemptCount: 5);

        // Policy returns null → budget exhausted
        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns((DateTimeOffset?)null);

        _notifications.Setup(r => r.MarkFailedAsync(
                TenantId, NotificationId, 1L, NotificationState.Sending,
                NotificationState.FailedPermanent, "retry_exhausted", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await BuildScheduler().HandleTransientAsync(n, expectedVersion: 1, "timeout", CancellationToken.None);

        // MarkFailedAsync(FailedPermanent, "retry_exhausted") called
        _notifications.Verify(r => r.MarkFailedAsync(
            TenantId, NotificationId, 1L, NotificationState.Sending,
            NotificationState.FailedPermanent, "retry_exhausted", It.IsAny<CancellationToken>()), Times.Once);

        // MarkTransientRetryAsync NOT called
        _notifications.Verify(r => r.MarkTransientRetryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // No scheduled_job added (Inv-8: no work-item for booking either)
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);

        // audit notification.retry_exhausted emitted
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_exhausted"),
            It.IsAny<CancellationToken>()), Times.Once);

        // No retry_scheduled audit
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_scheduled"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 3. Idempotent: job already exists → AddAsync not called ─────────────────────────────────

    [Fact]
    public async Task JobAlreadyExists_DoesNotAddDuplicate()
    {
        var n = MakeSendingNotification();
        var nextTime = DateTimeOffset.UtcNow.AddSeconds(5);

        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);

        _notifications.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Job already exists
        _jobs.Setup(j => j.ExistsAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await BuildScheduler().HandleTransientAsync(n, expectedVersion: 1, null, CancellationToken.None);

        // AddAsync NOT called (idempotent)
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 4. Lost race on the transient settle (0 rows) → no orphan job, no scheduled audit ────────

    [Fact]
    public async Task TransientSettleLosesRace_NoJobEnqueued_NoScheduledAudit()
    {
        var n = MakeSendingNotification();
        var nextTime = DateTimeOffset.UtcNow.AddSeconds(10);

        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);

        // Settle affects 0 rows → version/state moved under us (e.g. a racing receipt webhook).
        _notifications.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await BuildScheduler().HandleTransientAsync(n, expectedVersion: 1, "provider_timeout", CancellationToken.None);

        // No orphan retry job enqueued.
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);

        // No misleading retry_scheduled audit.
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_scheduled"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 5. Lost race on the exhaustion settle (0 rows) → no misleading exhausted audit ──────────

    [Fact]
    public async Task ExhaustionSettleLosesRace_NoExhaustedAudit()
    {
        var n = MakeSendingNotification(attemptCount: 5);

        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns((DateTimeOffset?)null);

        // Exhaustion settle affects 0 rows → FailedPermanent did not take effect.
        _notifications.Setup(r => r.MarkFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<NotificationState>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await BuildScheduler().HandleTransientAsync(n, expectedVersion: 1, "timeout", CancellationToken.None);

        // No misleading retry_exhausted audit (transition didn't take effect).
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.retry_exhausted"),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
