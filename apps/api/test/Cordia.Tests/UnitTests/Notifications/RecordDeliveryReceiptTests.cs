using Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="RecordDeliveryReceiptCommandHandler"/> (spec 10 §6.1, §12 FM#8, FM#11, T3).
/// All deps are mocked; the IUnitOfWork mock invokes the delegate so the handler body runs.
/// </summary>
[Trait("Category", "Unit")]
public class RecordDeliveryReceiptTests
{
    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<INotificationRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private const string TenantId = "tenant-receipt-test";
    private const string NotificationId = "notif-receipt-1";
    private const string ProviderMessageId = "pmsg_abc";

    private RecordDeliveryReceiptCommandHandler BuildHandler()
        => new(
            _repo.Object,
            _auditRepo.Object,
            _uow.Object,
            NullLogger<RecordDeliveryReceiptCommandHandler>.Instance,
            new Metrics());

    public RecordDeliveryReceiptTests()
    {
        // Make the UoW mock invoke the delegate so the handler body actually runs.
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> work, CancellationToken ct) => work(ct));

        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static Notification MakeNotification(
        NotificationState state,
        string? providerRef = ProviderMessageId,
        long version = 2)
        => new()
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            OperationId = "op-test",
            State = state,
            ProviderRef = providerRef,
            Version = version,
            Channel = NotificationChannel.Sms,
        };

    // ── 1. Accepted + Delivered → TryTransitionStateAsync(Accepted→Delivered), Applied, audit notification.delivered ──

    [Fact]
    public async Task AcceptedDelivered_TransitionsToDelivered_ReturnsApplied_AuditsDelivered()
    {
        var n = MakeNotification(NotificationState.Accepted, ProviderMessageId, version: 2);
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
            NotificationState.Accepted, NotificationState.Delivered, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.Applied, result);

        _repo.Verify(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
            NotificationState.Accepted, NotificationState.Delivered, It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.delivered"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 2. Accepted + Bounced → MarkFailedAsync(Accepted→FailedPermanent,"bounce"), Applied, audit notification.bounced ──

    [Fact]
    public async Task AcceptedBounced_TransitionsToFailedPermanent_ReturnsApplied_AuditsBounced()
    {
        var n = MakeNotification(NotificationState.Accepted, ProviderMessageId, version: 2);
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, NotificationId, 2L,
            NotificationState.Accepted, NotificationState.FailedPermanent, "bounce", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Bounced);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.Applied, result);

        _repo.Verify(r => r.MarkFailedAsync(TenantId, NotificationId, 2L,
            NotificationState.Accepted, NotificationState.FailedPermanent, "bounce", It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.bounced"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 3. Already Delivered → AlreadyTerminal; no transition call ──

    [Fact]
    public async Task AlreadyDelivered_ReturnsAlreadyTerminal_NoTransitionCall()
    {
        var n = MakeNotification(NotificationState.Delivered, ProviderMessageId);
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.AlreadyTerminal, result);

        _repo.Verify(r => r.TryTransitionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.MarkFailedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 4. provider_message_id mismatch → Mismatch; no transition call ──

    [Fact]
    public async Task ProviderMessageIdMismatch_ReturnsMismatch_NoTransitionCall()
    {
        var n = MakeNotification(NotificationState.Accepted, providerRef: "pmsg_OTHER");
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.Mismatch, result);

        _repo.Verify(r => r.TryTransitionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.MarkFailedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 5. Notification not found → NotFound ──

    [Fact]
    public async Task NotificationNotFound_ReturnsNotFound()
    {
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.NotFound, result);
    }

    // ── 6. Race: state Accepted but TryTransitionStateAsync returns 0; reload returns Delivered → AlreadyTerminal ──

    [Fact]
    public async Task Race_TransitionReturnsZero_ReloadReturnsDelivered_ReturnsAlreadyTerminal()
    {
        var n = MakeNotification(NotificationState.Accepted, ProviderMessageId, version: 2);
        var reloaded = MakeNotification(NotificationState.Delivered, ProviderMessageId, version: 3);

        // First GetAsync = Accepted row; second GetAsync (reload after lost race) = Delivered row.
        _repo.SetupSequence(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n)
            .ReturnsAsync(reloaded);

        // TryTransitionStateAsync returns 0 (lost race).
        _repo.Setup(r => r.TryTransitionStateAsync(TenantId, NotificationId, 2L,
            NotificationState.Accepted, NotificationState.Delivered, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.AlreadyTerminal, result);

        // Must NOT throw; no audit for delivered (lost the race).
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.delivered"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 7. State Sending (out of order) → OutOfOrder; no transition ──

    [Fact]
    public async Task StateSending_OutOfOrder_ReturnsOutOfOrder_NoTransition()
    {
        var n = MakeNotification(NotificationState.Sending, ProviderMessageId);
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        var cmd = new RecordDeliveryReceiptCommand(TenantId, NotificationId, ProviderMessageId, DeliveryReceiptStatus.Delivered);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(ReceiptOutcome.OutOfOrder, result);

        _repo.Verify(r => r.TryTransitionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.MarkFailedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<NotificationState>(), It.IsAny<NotificationState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
