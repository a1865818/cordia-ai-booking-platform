using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.SendNotification;
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
/// Unit tests for <see cref="SendNotificationCommandHandler"/> (spec 10 §4, §6.2, T2).
/// All deps are mocked; the handler is constructed and <c>Handle</c> is called directly.
/// </summary>
[Trait("Category", "Unit")]
public class SendNotificationTests
{
    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<INotificationRepository> _repo = new();
    private readonly Mock<INotificationRecipientResolver> _recipientResolver = new();
    private readonly Mock<IConsentGate> _consentGate = new();
    private readonly Mock<INotificationTemplateResolver> _templateResolver = new();
    private readonly Mock<INotificationProviderAdapter> _smsAdapter = new();
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    // T5a: retry scheduler deps
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<INotificationRepository> _retryRepo = new();
    private readonly Mock<IAuditEventRepository> _retryAuditRepo = new();

    private const string TenantId = "tenant-test";
    private const string OperationId = "op-test";
    private const string MessageId = "msg-test";

    private NotificationRetryScheduler BuildRetryScheduler() =>
        new(_jobs.Object, _retryPolicy.Object, _retryRepo.Object, _retryAuditRepo.Object,
            NullLogger<NotificationRetryScheduler>.Instance);

    private SendNotificationCommandHandler BuildHandler()
        => new(
            _repo.Object,
            _recipientResolver.Object,
            _consentGate.Object,
            _templateResolver.Object,
            new[] { _smsAdapter.Object },
            _opRepo.Object,
            _auditRepo.Object,
            BuildRetryScheduler(),
            NullLogger<SendNotificationCommandHandler>.Instance);

    private static BookingOperation DefaultOp(string tenantId, string operationId) => new()
    {
        TenantId = tenantId,
        OperationId = operationId,
        IntentId = "int-1",
        IntentRevision = 1,
        Status = OperationStatus.Booked,
        Version = 0,
    };

    private static ResolvedRecipient DefaultRecipient() =>
        new("recip_abc123", NotificationChannel.Sms, "en");

    private static NotificationTemplate DefaultTemplate() =>
        new("booking_confirmed.sms.v1", "Your booking is confirmed.");

    private SendNotificationCommand DefaultCommand() =>
        new(TenantId, OperationId, MessageId, NotificationEvent.BookingConfirmed, MessagePriority.High, null);

    // ── Test helpers ─────────────────────────────────────────────────────────────────────────────

    private void SetupHappyPath()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultRecipient());
        _consentGate.Setup(c => c.CheckAsync(TenantId, It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Allowed);
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── 1. Happy path ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ProviderAccepted_SettlesSendingToAccepted_AuditsAccepted()
    {
        // Arrange
        SetupHappyPath();
        _smsAdapter.Setup(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, "pmsg_abc", null));
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        var result = await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert
        Assert.Equal(Unit.Value, result);

        // Pending→Sending settle must be called once with expectedVersion=0
        _repo.Verify(r => r.TryTransitionStateAsync(TenantId, It.IsAny<string>(), 0L,
            NotificationState.Pending, NotificationState.Sending, It.IsAny<CancellationToken>()), Times.Once);

        // Sending→Accepted settle must be called once with expectedVersion=1
        _repo.Verify(r => r.MarkAcceptedAsync(TenantId, It.IsAny<string>(), 1L,
            NotificationState.Sending, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // SendAsync must have been called exactly once
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Once);

        // Audit "notification.accepted" must be added
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.accepted"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 2. Logical dedup — TryInsertAsync returns false ──────────────────────────────────────────

    [Fact]
    public async Task LogicalDedup_TryInsertFalse_DoesNotCallSendAsync_AuditsSuppressedDuplicate()
    {
        // Arrange
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultRecipient());
        _consentGate.Setup(c => c.CheckAsync(TenantId, It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Allowed);
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Dedup hit
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert: SendAsync NEVER called
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);

        // Audit "notification.suppressed_duplicate" added
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.suppressed_duplicate"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 3. Invalid recipient — resolver returns null ──────────────────────────────────────────────

    [Fact]
    public async Task InvalidRecipient_RecipientResolverReturnsNull_SettlesInvalidRecipient_NeverSends()
    {
        // Arrange
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        // Recipient resolver returns null
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedRecipient?)null);
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.InvalidRecipient, "no_destination", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert: settle Pending→InvalidRecipient called
        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.InvalidRecipient, "no_destination", It.IsAny<CancellationToken>()), Times.Once);

        // SendAsync NEVER called
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 4. Consent: Withdrawn → InvalidRecipient; Suppressed → SuppressedDuplicate ───────────────

    [Fact]
    public async Task ConsentWithdrawn_SettlesInvalidRecipient_NeverSends()
    {
        // Arrange
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultRecipient());
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.InvalidRecipient, "consent_withdrawn", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _consentGate.Setup(c => c.CheckAsync(TenantId, It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Withdrawn);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.InvalidRecipient, "consent_withdrawn", It.IsAny<CancellationToken>()), Times.Once);
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsentSuppressed_SettlesSuppressedDuplicate_NeverSends()
    {
        // Arrange
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultRecipient());
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.SuppressedDuplicate, "policy_suppressed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _consentGate.Setup(c => c.CheckAsync(TenantId, It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Suppressed);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending,
            NotificationState.SuppressedDuplicate, "policy_suppressed", It.IsAny<CancellationToken>()), Times.Once);
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 5. Transient failure — T5a: scheduler schedules retry; booking never touched (Inv-8) ──────

    [Fact]
    public async Task TransientFailure_SchedulesRetry_NeverTouchesBookingOp()
    {
        // Arrange: happy-path setup + transient adapter result
        SetupHappyPath();
        _smsAdapter.Setup(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.FailedTransient, null, "provider_timeout"));

        var nextTime = DateTimeOffset.UtcNow.AddSeconds(5);
        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(nextTime);
        _retryRepo.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                NotificationState.Sending, nextTime, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _retryAuditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert: MarkTransientRetryAsync called via scheduler (budget not exhausted)
        _retryRepo.Verify(r => r.MarkTransientRetryAsync(
            It.IsAny<string>(), It.IsAny<string>(), 1L,
            NotificationState.Sending, nextTime, It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert: scheduled_job enqueued for retry
        _jobs.Verify(j => j.AddAsync(
            It.Is<ScheduledJob>(job => job.JobType == "notification.retry.requested.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Inv-8: MarkFailedAsync(FailedTransient) never called directly on _repo
        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
            NotificationState.FailedTransient, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Inv-8: booking operations NEVER touched
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 6. Permanent failure ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PermanentFailure_SettlesFailedPermanent()
    {
        // Arrange
        SetupHappyPath();
        _smsAdapter.Setup(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.FailedPermanent, null, "hard_reject"));
        _repo.Setup(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
            NotificationState.FailedPermanent, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert: FailedPermanent settle called
        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
            NotificationState.FailedPermanent, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Inv-8: booking op never touched
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 7. No adapter for channel — poison-loop guard settles FailedPermanent without throwing ──────

    [Fact]
    public async Task NoAdapterForChannel_SettlesFailedPermanent_NeverSends_DoesNotThrow()
    {
        // Arrange: recipient resolves to Email, but only the Sms adapter is registered in the fixture.
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOp(TenantId, OperationId));
        _recipientResolver.Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient("recip_email", NotificationChannel.Email, "en"));
        _consentGate.Setup(c => c.CheckAsync(TenantId, It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Allowed);
        _templateResolver.Setup(t => t.ResolveAsync(It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplate());
        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, It.IsAny<string>(), 0L, NotificationState.Pending, NotificationState.Sending, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
            NotificationState.FailedPermanent, "no_adapter_for_channel", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        // Fixture's adapter list contains only the Sms adapter.
        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — must NOT throw (poison-loop guard).
        var result = await BuildHandler().Handle(DefaultCommand(), CancellationToken.None);

        // Assert
        Assert.Equal(Unit.Value, result);

        // Settle Sending→FailedPermanent with "no_adapter_for_channel" called once.
        _repo.Verify(r => r.MarkFailedAsync(TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
            NotificationState.FailedPermanent, "no_adapter_for_channel", It.IsAny<CancellationToken>()), Times.Once);

        // SendAsync NEVER called.
        _smsAdapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);

        // Audit "notification.failed_permanent" added.
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.failed_permanent"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
