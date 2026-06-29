using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.Retry;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="RetryNotificationCommandHandler"/> (spec 10 FM#6, T5a).
/// </summary>
[Trait("Category", "Unit")]
public class RetryNotificationTests
{
    private const string TenantId = "tenant-retry-handler";
    private const string NotificationId = "notif-retry-handler-1";
    private const string MessageId = "msg-retry-handler-1";
    private const string OperationId = "op-retry-handler-1";
    private const string ProviderKey = "pkey-retry-handler-1";

    private readonly Mock<INotificationRepository> _repo = new();
    private readonly Mock<INotificationProviderAdapter> _smsAdapter = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<INotificationRepository> _retryNotifRepo = new();
    private readonly Mock<IAuditEventRepository> _retryAuditRepo = new();
    private readonly Mock<INotificationTemplateResolver> _templateResolver = new();

    public RetryNotificationTests()
    {
        _smsAdapter.Setup(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _retryAuditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Default template resolver returns a stub with a non-empty body (F10).
        _templateResolver.Setup(r => r.ResolveAsync(
                It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("v1", "stub-body"));
    }

    private NotificationRetryScheduler BuildScheduler() =>
        new(_jobs.Object, _retryPolicy.Object, _retryNotifRepo.Object, _retryAuditRepo.Object,
            NullLogger<NotificationRetryScheduler>.Instance);

    private RetryNotificationCommandHandler BuildHandler() =>
        new(
            _repo.Object,
            new[] { _smsAdapter.Object },
            _auditRepo.Object,
            BuildScheduler(),
            _templateResolver.Object,
            NullLogger<RetryNotificationCommandHandler>.Instance);

    private static Notification MakeFailedTransientNotification(long version = 2) => new()
    {
        TenantId = TenantId,
        NotificationId = NotificationId,
        OperationId = OperationId,
        State = NotificationState.FailedTransient,
        Channel = NotificationChannel.Sms,
        Priority = MessagePriority.High,
        ProviderIdempotencyKey = ProviderKey,
        RecipientRef = "recip-retry",
        TemplateVersion = "v1",
        MaterialRevision = 1,
        AttemptCount = 1,
        NextEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1), // already past
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = version,
    };

    private RetryNotificationCommand DefaultCommand() =>
        new(TenantId, NotificationId, MessageId);

    // ── 1. FailedTransient + due + adapter Accepted → FailedTransient→Sending→Accepted ──────────

    [Fact]
    public async Task FailedTransient_Due_AdapterAccepted_TransitionsToAccepted()
    {
        var n = MakeFailedTransientNotification(version: 2);

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        // FailedTransient → Sending (version 2 → 3)
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _smsAdapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, "pmsg_retry_1", null));

        // Sending → Accepted (sendingVersion = 3)
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 3L,
                NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        Assert.Equal(Unit.Value, result);

        // FailedTransient → Sending claimed once
        _repo.Verify(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
            NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()), Times.Once);

        // Sending → Accepted settled with sendingVersion=3
        _repo.Verify(r => r.MarkAcceptedAsync(TenantId, NotificationId, 3L,
            NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.accepted"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 2. Not FailedTransient → no-op ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NotFailedTransient_NoOp_NoAdapterCalls()
    {
        var n = new Notification
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            State = NotificationState.Accepted,
            Channel = NotificationChannel.Sms,
            Version = 2,
        };

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        _smsAdapter.Verify(a => a.SendAsync(
            It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.TryTransitionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 3. FailedTransient + adapter transient → HandleTransientAsync invoked ──────────────────

    [Fact]
    public async Task FailedTransient_AdapterTransient_InvokesRetryScheduler()
    {
        var n = MakeFailedTransientNotification(version: 2);

        // Note: GetAsync is configured via SetupSequence below (load row, then reload after the
        // Sending transition) — no standalone Setup needed here.
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _smsAdapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.FailedTransient, null, "timeout"));

        // reload after Sending transition
        var reloaded = new Notification
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            OperationId = OperationId,
            State = NotificationState.Sending,
            Channel = NotificationChannel.Sms,
            Priority = MessagePriority.High,
            AttemptCount = 1,
            CreatedAt = n.CreatedAt,
            Version = 3, // after the FailedTransient→Sending bump
            ProviderIdempotencyKey = ProviderKey,
            RecipientRef = "recip-retry",
            TemplateVersion = "v1",
        };
        _repo.SetupSequence(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n)      // first call: load row
            .ReturnsAsync(reloaded); // second call: reload after Sending transition

        var nextTime = DateTimeOffset.UtcNow.AddSeconds(15);
        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);

        _retryNotifRepo.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // retryScheduler.MarkTransientRetryAsync was invoked (via retryNotifRepo mock)
        _retryNotifRepo.Verify(r => r.MarkTransientRetryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            NotificationState.Sending, nextTime, It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // scheduled_job enqueued
        _jobs.Verify(j => j.AddAsync(
            It.Is<ScheduledJob>(job => job.JobType == "notification.retry.requested.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 4. No adapter → FailedPermanent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoAdapter_SettlesFailedPermanent_NeverSends()
    {
        var n = MakeFailedTransientNotification(version: 2);

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _repo.Setup(r => r.MarkFailedAsync(TenantId, NotificationId, 3L,
                NotificationState.Sending, NotificationState.FailedPermanent, "no_adapter_for_channel",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Build handler with NO adapters
        var handler = new RetryNotificationCommandHandler(
            _repo.Object,
            Array.Empty<INotificationProviderAdapter>(),
            _auditRepo.Object,
            BuildScheduler(),
            _templateResolver.Object,
            NullLogger<RetryNotificationCommandHandler>.Instance);

        await handler.Handle(DefaultCommand(), CancellationToken.None);

        _repo.Verify(r => r.MarkFailedAsync(TenantId, NotificationId, 3L,
            NotificationState.Sending, NotificationState.FailedPermanent, "no_adapter_for_channel",
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.failed_permanent"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 5. F2: settle returns 0 (lost race) → audit NOT emitted ────────────────────────────────

    [Fact]
    public async Task SettleReturns0_LostRace_AcceptedAuditNotEmitted_F2()
    {
        var n = MakeFailedTransientNotification(version: 2);

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _smsAdapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, "pmsg_abc", null));

        // Settle returns 0 — lost race.
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 3L,
                NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // F2: audit NOT emitted when settle returned 0.
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.accepted"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 6. F9: adapter throws → treated as transient (no exception propagation) ──────────────────

    [Fact]
    public async Task AdapterThrows_TreatedAsTransient_NoExceptionPropagated_F9()
    {
        var n = MakeFailedTransientNotification(version: 2);

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Adapter throws.
        _smsAdapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider boom"));

        var nextTime = DateTimeOffset.UtcNow.AddSeconds(5);
        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);
        _retryNotifRepo.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                NotificationState.Sending, nextTime, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _retryAuditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // GetAsync is called again by HandleTransientAsync path (reload).
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        // F9: must NOT throw.
        var ex = await Record.ExceptionAsync(() => BuildHandler().Handle(DefaultCommand(), CancellationToken.None));
        Assert.Null(ex);

        // HandleTransientAsync called (MarkTransientRetryAsync path).
        _retryNotifRepo.Verify(r => r.MarkTransientRetryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            NotificationState.Sending, nextTime, It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 7. F10: retry resend uses non-empty Body from template ────────────────────────────────────

    [Fact]
    public async Task RetryResend_UsesNonEmptyBodyFromTemplate_F10()
    {
        const string expectedBody = "Your booking is confirmed (retry).";
        _templateResolver.Setup(r => r.ResolveAsync(
                NotificationEvent.BookingConfirmed, NotificationChannel.Sms,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("v1", expectedBody));

        var n = MakeFailedTransientNotification(version: 2);
        n.EventType = NotificationEvent.BookingConfirmed;

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
                NotificationState.FailedTransient, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        RenderedMessage? capturedMessage = null;
        _smsAdapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, RenderedMessage, CancellationToken>((_, msg, _) => capturedMessage = msg)
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, "pmsg_r2", null));

        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 3L,
                NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        Assert.NotNull(capturedMessage);
        // F10: body must not be empty — it must be the template body.
        Assert.Equal(expectedBody, capturedMessage!.Body);
    }
}
