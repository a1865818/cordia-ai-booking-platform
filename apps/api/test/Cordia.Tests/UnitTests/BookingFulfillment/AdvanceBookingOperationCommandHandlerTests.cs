using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;
using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Application.Handlers.BookingFulfillment.RequestPayment;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Application.Handlers.BookingFulfillment.Validate;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Moq;
using System.Text.Json;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for the booking operation driver loop (spec 06 §6.2 status→step mapping).
/// Tests verify routing decisions and loop termination without testing step handler logic
/// (which is covered by the E2E integration test).
/// </summary>
[Trait("Category", "Unit")]
public class AdvanceBookingOperationCommandHandlerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IInboxRepository> _inboxRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IDeadLetterHandler> _deadLetterHandler = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();

    private AdvanceBookingOperationCommandHandler CreateHandler() =>
        new(_mediator.Object, _opRepo.Object, _inboxRepo.Object, _uow.Object,
            _deadLetterHandler.Object, _workItemRepo.Object, _outboxRepo.Object, _auditRepo.Object);

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

    public AdvanceBookingOperationCommandHandlerTests()
    {
        // UoW runs the work delegate immediately (no real transaction in unit test).
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Inbox TryAdmit always returns true (first admit).
        _inboxRepo.Setup(r => r.TryAdmitAsync(It.IsAny<ConsumerInbox>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Default DLQ / work-item / outbox / audit no-ops (unused in most routing tests).
        _deadLetterHandler.Setup(d => d.QuarantineAsync(
                It.IsAny<Domain.Messaging.JobEnvelope>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);
        _outboxRepo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Handle_AcceptedStatus_RoutesToValidateOperationCommand()
    {
        var tenantId = "t-test";
        var operationId = "op-1";
        var op = MakeOp(tenantId, operationId, OperationStatus.Accepted);

        // First call returns Accepted op; subsequent calls return Booked (terminal) to stop the loop.
        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op)
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        var command = new AdvanceBookingOperationCommand(tenantId, operationId, "msg-1");

        await handler.Handle(command, CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is ValidateOperationCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidatingStatus_RoutesToValidateOperationCommand()
    {
        var tenantId = "t-test";
        var operationId = "op-2";

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Validating))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-2"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is ValidateOperationCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ReservingCapacityStatus_RoutesToReserveCapacityCommand()
    {
        var tenantId = "t-test";
        var operationId = "op-3";

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.ReservingCapacity))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-3"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is ReserveCapacityCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HeldStatus_RoutesToRequestPaymentCommand()
    {
        // T7: Held now routes to RequestPaymentCommand (deposit decision seam).
        var tenantId = "t-test";
        var operationId = "op-4";

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Held))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-4"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is RequestPaymentCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_FinalizingStatus_RoutesToFinalizeBookingCommand()
    {
        var tenantId = "t-test";
        var operationId = "op-5";

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Finalizing))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-5"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is FinalizeBookingCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_TerminalStatus_NoStepCommandDispatched()
    {
        var tenantId = "t-test";
        var operationId = "op-6";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-6"), CancellationToken.None);

        _mediator.Verify(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WaitOutcome_LoopStops()
    {
        var tenantId = "t-test";
        var operationId = "op-7";

        // Returns Accepted every time — but the step handler returns Wait so the loop stops after 1 iteration.
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Accepted));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Wait);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-7"), CancellationToken.None);

        // The step was dispatched only once.
        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is ValidateOperationCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_TerminalOutcome_LoopStops()
    {
        var tenantId = "t-test";
        var operationId = "op-8";

        // T7: Held routes to RequestPaymentCommand; Terminal breaks the loop after one dispatch.
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Held));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-8"), CancellationToken.None);

        // Dispatched exactly once despite op still appearing as Held (Terminal breaks the loop).
        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is RequestPaymentCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WaitingStatus_BreaksLoop()
    {
        // AwaitingUserAction is not a step the driver handles → loop breaks without dispatching.
        var tenantId = "t-test";
        var operationId = "op-9";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.AwaitingUserAction));

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-9"), CancellationToken.None);

        _mediator.Verify(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NullOperation_ReturnsWithoutDispatching()
    {
        var tenantId = "t-test";
        var operationId = "op-null";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-null"), CancellationToken.None);

        _mediator.Verify(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_TenantMismatch_ReturnsWithoutDispatching()
    {
        var tenantId = "t-test";
        var operationId = "op-tenant";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp("different-tenant", operationId, OperationStatus.Accepted));

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-t"), CancellationToken.None);

        _mediator.Verify(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AwaitingPaymentStatus_BreaksLoop()
    {
        // T7: AwaitingPayment is a wait state — loop breaks without dispatching (external event driven).
        var tenantId = "t-test";
        var operationId = "op-awp";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.AwaitingPayment));

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-awp"), CancellationToken.None);

        _mediator.Verify(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FinalizingStatus_AfterNoDepositPath_RoutesToFinalizeBookingCommand()
    {
        // T7: Finalizing still routes directly to FinalizeBookingCommand (no-deposit path lands here).
        var tenantId = "t-test";
        var operationId = "op-fin";

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Finalizing))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Booked));

        _mediator.Setup(m => m.Send(It.IsAny<IRequest<StepOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepOutcome.Terminal);

        var handler = CreateHandler();
        await handler.Handle(new AdvanceBookingOperationCommand(tenantId, operationId, "msg-fin"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IRequest<StepOutcome>>(r => r is FinalizeBookingCommand),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
