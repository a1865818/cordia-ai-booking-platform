using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.RequestPayment;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for the RequestPaymentCommandHandler deposit decision seam (T7, spec 06 §6.1).
/// </summary>
[Trait("Category", "Unit")]
public class RequestPaymentCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IDepositPolicy> _depositPolicy = new();
    private readonly Mock<IDepositSessionRepository> _depositSessionRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private RequestPaymentCommandHandler CreateHandler() => new(
        _opRepo.Object,
        _depositPolicy.Object,
        _depositSessionRepo.Object,
        _stepRepo.Object,
        _outboxRepo.Object,
        _auditRepo.Object,
        _uow.Object);

    private static BookingOperation MakeOp(string tenantId, string operationId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = version,
        };

    public RequestPaymentCommandHandlerTests()
    {
        // UoW runs delegate immediately.
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _stepRepo.Setup(r => r.GetOrAddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep candidate, CancellationToken _) => candidate);
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _depositSessionRepo.Setup(r => r.AddAsync(It.IsAny<DepositSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Default: transition succeeds.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    [Fact]
    public async Task Handle_NoDepositRequired_TransitionsHeldToFinalizing()
    {
        var tenantId = "t-test";
        var operationId = "op-1";
        var op = MakeOp(tenantId, operationId, OperationStatus.Held);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DepositRequirement.None);

        var handler = CreateHandler();
        var result = await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.Held, OperationStatus.Finalizing,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NoDepositRequired_DoesNotCreateDepositSession()
    {
        var tenantId = "t-test";
        var operationId = "op-1b";
        var op = MakeOp(tenantId, operationId, OperationStatus.Held);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DepositRequirement.None);

        var handler = CreateHandler();
        await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        _depositSessionRepo.Verify(r => r.AddAsync(It.IsAny<DepositSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DepositRequired_TransitionsHeldToAwaitingPayment_ReturnsWait()
    {
        var tenantId = "t-test";
        var operationId = "op-2";
        var op = MakeOp(tenantId, operationId, OperationStatus.Held);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DepositRequirement(true, 5000, "AUD"));

        var handler = CreateHandler();
        var result = await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.Held, OperationStatus.AwaitingPayment,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DepositRequired_CreatesDepositSession()
    {
        var tenantId = "t-test";
        var operationId = "op-2b";
        var op = MakeOp(tenantId, operationId, OperationStatus.Held);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DepositRequirement(true, 5000, "AUD"));

        var handler = CreateHandler();
        await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        _depositSessionRepo.Verify(r => r.AddAsync(
            It.Is<DepositSession>(s =>
                s.TenantId == tenantId &&
                s.OperationId == operationId &&
                s.AmountMinor == 5000 &&
                s.Currency == "AUD" &&
                s.Status == DepositStatus.Created),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DepositRequired_EmitsDepositLinkRequestedOutbox()
    {
        var tenantId = "t-test";
        var operationId = "op-2c";
        var op = MakeOp(tenantId, operationId, OperationStatus.Held);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DepositRequirement(true, 5000, "AUD"));

        var handler = CreateHandler();
        await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "payment.deposit_link.requested.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_OpNotHeld_ReturnsContinueWithoutTransition()
    {
        var tenantId = "t-test";
        var operationId = "op-3";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.AwaitingPayment));
        _depositPolicy.Setup(p => p.GetRequirementAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DepositRequirement.None);

        var handler = CreateHandler();
        var result = await handler.Handle(new RequestPaymentCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
