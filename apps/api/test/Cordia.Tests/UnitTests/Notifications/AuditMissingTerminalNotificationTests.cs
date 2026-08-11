using System.Diagnostics.Metrics;
using Cordia.Application.Handlers.Notifications.MissingTerminalAudit;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="AuditMissingTerminalNotificationCommandHandler"/>
/// (spec 10 §25.4 / FM#12 / Inv-14, T5b).
///
/// All deps are mocked; the IUnitOfWork mock invokes the delegate so the handler body runs.
/// A MeterListener observes the Cordia meter so the missing-terminal alert counter (spec 13)
/// can be asserted.
/// </summary>
[Trait("Category", "Unit")]
public class AuditMissingTerminalNotificationTests : IDisposable
{
    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Metrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = new();

    private const string TenantId = "tenant-missing-notif-test";
    private const string OperationId = "op-missing-notif-1";

    public AuditMissingTerminalNotificationTests()
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

        // Make the UoW mock invoke the delegate so the handler body actually runs.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> work, CancellationToken ct) => work(ct));


        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private AuditMissingTerminalNotificationCommandHandler BuildHandler()
        => new(_workItemRepo.Object, _notifications.Object, _audit.Object, _uow.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditMissingTerminalNotificationCommandHandler>.Instance,
            _metrics);

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    // ── 1. Happy path: no existing work item + no accepted notification → creates work item + audit ──

    [Fact]
    public async Task NoOpenWorkItem_NoAcceptedNotification_CreatesWorkItemAndAudit_ReturnsTrue()
    {
        // No open work item for this op.
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        // No accepted terminal notification.
        _notifications.Setup(r => r.HasAcceptedTerminalNotificationAsync(TenantId, OperationId,
                It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // TryAddOpenAsync returns true (insert wins).
        _workItemRepo.Setup(r => r.TryAddOpenAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cmd = new AuditMissingTerminalNotificationCommand(TenantId, OperationId, OperationStatus.Booked);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.True(result);

        // Work item created (via TryAddOpenAsync) with reason="missing_notification" and state="open".
        _workItemRepo.Verify(r => r.TryAddOpenAsync(
            It.Is<OperationWorkItem>(wi =>
                wi.TenantId == TenantId &&
                wi.OperationId == OperationId &&
                wi.Reason == "missing_notification" &&
                wi.State == "open"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Audit event with type "notification.missing_terminal".
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e =>
                e.TenantId == TenantId &&
                e.OperationId == OperationId &&
                e.EventType == "notification.missing_terminal" &&
                e.Actor == "system"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Inv-8/9 alert signal (spec 13 §25.4): missing-terminal counter incremented.
        Assert.Equal(1, _counters.GetValueOrDefault("notification.missing_terminal_total"));
    }

    // ── 2. Open work item already exists → idempotency short-circuit, no new work item, returns false ──

    [Fact]
    public async Task OpenWorkItemExists_ReturnsFalse_NoAddAsync_NoAudit()
    {
        // An open work item already exists.
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationWorkItem
            {
                TenantId = TenantId,
                WorkItemId = "wi_existing",
                OperationId = OperationId,
                Reason = "missing_notification",
                State = "open",
            });

        var cmd = new AuditMissingTerminalNotificationCommand(TenantId, OperationId, OperationStatus.Booked);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.False(result);

        // No new work item should be created.
        _workItemRepo.Verify(r => r.TryAddOpenAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No audit event.
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No alert signal when the work item is not raised.
        Assert.Equal(0, _counters.GetValueOrDefault("notification.missing_terminal_total"));
    }

    // ── 3. Defensive re-check: notification accepted between list sweep and now → returns false ──

    [Fact]
    public async Task NotificationAcceptedBetweenSweepAndNow_ReturnsFalse_NoWorkItem()
    {
        // No open work item (idempotency check passes).
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        // Defensive re-check: the specific terminal notification is now accepted.
        _notifications.Setup(r => r.HasAcceptedTerminalNotificationAsync(TenantId, OperationId,
                It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cmd = new AuditMissingTerminalNotificationCommand(TenantId, OperationId, OperationStatus.Booked);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        Assert.False(result);

        // No work item should be created (notification arrived in time).
        _workItemRepo.Verify(r => r.TryAddOpenAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No audit event.
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 4. F4: Booked op with StillPending only (no BookingConfirmed) → IS flagged missing ───────

    [Fact]
    public async Task BookedOp_WithOnlyStillPendingAccepted_IsFlaggedMissing()
    {
        // No open work item.
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);

        // Defensive re-check for BookingConfirmed returns false (only StillPending is accepted, not BookingConfirmed).
        _notifications.Setup(r => r.HasAcceptedTerminalNotificationAsync(TenantId, OperationId,
                NotificationEvent.BookingConfirmed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // TryAddOpenAsync returns true (insert wins).
        _workItemRepo.Setup(r => r.TryAddOpenAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cmd = new AuditMissingTerminalNotificationCommand(TenantId, OperationId, OperationStatus.Booked);
        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        // The op should be flagged (no BookingConfirmed accepted yet).
        Assert.True(result);

        _workItemRepo.Verify(r => r.TryAddOpenAsync(
            It.Is<OperationWorkItem>(wi => wi.Reason == "missing_notification" && wi.State == "open"),
            It.IsAny<CancellationToken>()), Times.Once);

        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.missing_terminal"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
