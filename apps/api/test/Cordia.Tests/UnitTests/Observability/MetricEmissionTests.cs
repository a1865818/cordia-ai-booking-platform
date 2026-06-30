using System.Diagnostics.Metrics;
using Cordia.Application.Behaviours;
using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.SendNotification;
using Cordia.Application.Messaging;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Observability;

/// <summary>
/// Verifies that key handler paths emit the expected metric increments.
/// Uses MeterListener to observe counter increments synchronously.
/// </summary>
[Trait("Category", "Unit")]
public class MetricEmissionTests : IDisposable
{
    private readonly Metrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = new();

    public MetricEmissionTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _metrics.Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            _counters.TryGetValue(inst.Name, out var current);
            _counters[inst.Name] = current + measurement;
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    // ── Test 1: InboxDeduplicationBehavior dedup path emits both dedup counters ──

    [Fact]
    public async Task InboxDedup_DuplicatePath_EmitsDedupAndDeliveryDuplicateCounters()
    {
        // Arrange
        var uow = new Mock<IUnitOfWork>();
        var inboxRepo = new Mock<IInboxRepository>();

        // Make TryAdmitAsync return false (duplicate)
        inboxRepo.Setup(r => r.TryAdmitAsync(It.IsAny<ConsumerInbox>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // ExecuteInTransactionAsync runs the lambda synchronously in tests
        uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((fn, ct) => fn(ct));

        var behavior = new InboxDeduplicationBehavior<TestInboxRequest, Unit>(uow.Object, inboxRepo.Object, _metrics);

        var request = new TestInboxRequest();
        var nextCalled = false;
        Task<Unit> Next(CancellationToken _) { nextCalled = true; return Task.FromResult(Unit.Value); }

        // Act
        await behavior.Handle(request, Next, CancellationToken.None);

        // Assert
        Assert.False(nextCalled, "handler next() should not be called on duplicate");
        Assert.Equal(1, _counters.GetValueOrDefault("fulfillment.inbox.dedup_suppressed_total"));
        Assert.Equal(1, _counters.GetValueOrDefault("fulfillment.delivery.duplicate_total"));
    }

    // ── Test 2: SendNotificationCommandHandler dedup path (TryInsertAsync returns false) ──

    [Fact]
    public async Task SendNotification_DuplicateInsert_EmitsNotificationDuplicateSuppressedCounter()
    {
        // Arrange
        var repo = new Mock<INotificationRepository>();
        var recipientResolver = new Mock<INotificationRecipientResolver>();
        var consentGate = new Mock<IConsentGate>();
        var templateResolver = new Mock<INotificationTemplateResolver>();
        var opRepo = new Mock<IBookingOperationRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var retryScheduler = BuildRetryScheduler();

        var op = new BookingOperation
        {
            TenantId = "t1",
            OperationId = "op1",
            IntentId = "intent1",
            IntentRevision = 1,
            Status = OperationStatus.Booked,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 0,
        };
        opRepo.Setup(r => r.GetAsync("op1", It.IsAny<CancellationToken>())).ReturnsAsync(op);

        templateResolver.Setup(r => r.ResolveAsync(
                It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("v1", "body"));

        recipientResolver.Setup(r => r.ResolveAsync("t1", "op1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient("+1234", NotificationChannel.Sms, "en"));

        // Existing row → insert returns false (duplicate)
        repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new SendNotificationCommandHandler(
            repo.Object, recipientResolver.Object, consentGate.Object,
            templateResolver.Object, Array.Empty<INotificationProviderAdapter>(),
            opRepo.Object, auditRepo.Object, retryScheduler,
            NullLogger<SendNotificationCommandHandler>.Instance,
            _metrics);

        var command = new SendNotificationCommand("t1", "op1", "msg1", NotificationEvent.BookingConfirmed, MessagePriority.High, null);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(1, _counters.GetValueOrDefault("notification.duplicate_suppressed_total"));
    }

    // ── Test 3: SendNotificationCommandHandler invalid-recipient path ──

    [Fact]
    public async Task SendNotification_NoRecipient_EmitsInvalidRecipientCounter()
    {
        // Arrange
        var repo = new Mock<INotificationRepository>();
        var recipientResolver = new Mock<INotificationRecipientResolver>();
        var consentGate = new Mock<IConsentGate>();
        var templateResolver = new Mock<INotificationTemplateResolver>();
        var opRepo = new Mock<IBookingOperationRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var retryScheduler = BuildRetryScheduler();

        var op = new BookingOperation
        {
            TenantId = "t1",
            OperationId = "op2",
            IntentId = "intent2",
            IntentRevision = 1,
            Status = OperationStatus.Booked,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 0,
        };
        opRepo.Setup(r => r.GetAsync("op2", It.IsAny<CancellationToken>())).ReturnsAsync(op);

        templateResolver.Setup(r => r.ResolveAsync(
                It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("v1", "body"));

        // Recipient is null → invalid recipient path
        recipientResolver.Setup(r => r.ResolveAsync("t1", "op2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedRecipient?)null);

        // Insert succeeds (first time)
        repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        repo.Setup(r => r.MarkFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<NotificationState>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new SendNotificationCommandHandler(
            repo.Object, recipientResolver.Object, consentGate.Object,
            templateResolver.Object, Array.Empty<INotificationProviderAdapter>(),
            opRepo.Object, auditRepo.Object, retryScheduler,
            NullLogger<SendNotificationCommandHandler>.Instance,
            _metrics);

        var command = new SendNotificationCommand("t1", "op2", "msg2", NotificationEvent.BookingConfirmed, MessagePriority.High, null);

        // Act
        await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(1, _counters.GetValueOrDefault("notification.invalid_recipient_total"));
    }

    // ── Helpers ──

    private NotificationRetryScheduler BuildRetryScheduler()
    {
        var jobs = new Mock<IScheduledJobRepository>();
        var retryPolicy = new Mock<IRetryPolicy>();
        var notifRepo = new Mock<INotificationRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        return new NotificationRetryScheduler(
            jobs.Object, retryPolicy.Object, notifRepo.Object, auditRepo.Object,
            NullLogger<NotificationRetryScheduler>.Instance);
    }
}

// ── Minimal IInboxConsumerRequest stub for InboxDeduplicationBehavior tests ──
internal sealed class TestInboxRequest : IInboxConsumerRequest
{
    public string TenantId => "tenant-test";
    public string ConsumerName => "test.consumer";
    public string MessageId => "msg-dedup-test";
    public string? CorrelationId => "corr-test";
    public string? OperationId => "op-test";
}
