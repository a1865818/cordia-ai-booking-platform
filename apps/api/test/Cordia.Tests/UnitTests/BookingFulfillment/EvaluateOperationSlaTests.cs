using Cordia.Application.Handlers.BookingFulfillment.Sla;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

[Trait("Category", "Unit")]
public class EvaluateOperationSlaTests
{
    private static Mock<IBookingOperationRepository> OpRepo(BookingOperation? op)
    {
        var mock = new Mock<IBookingOperationRepository>();
        mock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        return mock;
    }

    private static Mock<IUnitOfWork> UowPassThrough()
    {
        var mock = new Mock<IUnitOfWork>();
        mock.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((fn, ct) => fn(ct));
        return mock;
    }

    private static BookingOperation MakeOp(string tenantId, string operationId, OperationStatus status, DateTimeOffset updatedAt)
        => new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = 0,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };

    // ── Threshold tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finalizing_ElapsedUnder30s_NoAction()
    {
        var op = MakeOp("t", "op1", OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddSeconds(-10));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op1"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        workItemRepo.Verify(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Finalizing_Elapsed45s_PendingNotification()
    {
        var op = MakeOp("t", "op2", OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddSeconds(-45));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        outboxRepo.Setup(r => r.ExistsAsync("t", $"obx_sla_pending_op2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op2"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.still_confirming.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
        workItemRepo.Verify(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Finalizing_Elapsed20min_Escalation()
    {
        var op = MakeOp("t", "op3", OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddMinutes(-20));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        outboxRepo.Setup(r => r.ExistsAsync("t", $"obx_sla_esc_op3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        workItemRepo.Setup(r => r.GetOpenByOperationAsync("t", "op3", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op3"), CancellationToken.None);

        workItemRepo.Verify(r => r.AddAsync(
            It.Is<OperationWorkItem>(w => w.State == "open" && w.Reason == "sla_escalation"),
            It.IsAny<CancellationToken>()), Times.Once);
        outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.sla_escalation.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reconciling_Elapsed1s_PendingNotification()
    {
        // Reconciling has pending threshold = 0 (immediate)
        var op = MakeOp("t", "op4", OperationStatus.Reconciling, DateTimeOffset.UtcNow.AddSeconds(-1));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        outboxRepo.Setup(r => r.ExistsAsync("t", $"obx_sla_pending_op4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op4"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.still_confirming.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reconciling_Elapsed20min_Escalation()
    {
        // Reconciling escalation = 900s
        var op = MakeOp("t", "op5", OperationStatus.Reconciling, DateTimeOffset.UtcNow.AddMinutes(-20));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        outboxRepo.Setup(r => r.ExistsAsync("t", $"obx_sla_esc_op5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        workItemRepo.Setup(r => r.GetOpenByOperationAsync("t", "op5", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op5"), CancellationToken.None);

        workItemRepo.Verify(r => r.AddAsync(
            It.Is<OperationWorkItem>(w => w.State == "open" && w.Reason == "sla_escalation"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompensationRequired_ElapsedUnder1800s_NoAction()
    {
        // CompensationRequired: no pending, escalation = 1800s
        var op = MakeOp("t", "op6", OperationStatus.CompensationRequired, DateTimeOffset.UtcNow.AddMinutes(-20));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op6"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        workItemRepo.Verify(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompensationRequired_Elapsed35min_Escalation()
    {
        // CompensationRequired: escalation = 1800s (30 min); 35 min > 1800s
        var op = MakeOp("t", "op7", OperationStatus.CompensationRequired, DateTimeOffset.UtcNow.AddMinutes(-35));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        outboxRepo.Setup(r => r.ExistsAsync("t", $"obx_sla_esc_op7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        workItemRepo.Setup(r => r.GetOpenByOperationAsync("t", "op7", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op7"), CancellationToken.None);

        workItemRepo.Verify(r => r.AddAsync(
            It.Is<OperationWorkItem>(w => w.State == "open" && w.Reason == "sla_escalation"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UserWaitState_NoRepoWrites()
    {
        var op = MakeOp("t", "op8", OperationStatus.AwaitingPayment, DateTimeOffset.UtcNow.AddMinutes(-20));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op8"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        workItemRepo.Verify(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TerminalState_NoRepoWrites()
    {
        var op = MakeOp("t", "op9", OperationStatus.Booked, DateTimeOffset.UtcNow.AddMinutes(-20));
        var opRepo = OpRepo(op);
        var outboxRepo = new Mock<IOutboxRepository>();
        var workItemRepo = new Mock<IOperationWorkItemRepository>();
        var auditRepo = new Mock<IAuditEventRepository>();
        var uow = UowPassThrough();

        var handler = new EvaluateOperationSlaCommandHandler(
            opRepo.Object, workItemRepo.Object, outboxRepo.Object, auditRepo.Object, uow.Object);

        await handler.Handle(new EvaluateOperationSlaCommand("t", "op9"), CancellationToken.None);

        outboxRepo.Verify(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        workItemRepo.Verify(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
