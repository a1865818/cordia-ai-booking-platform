using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Validate;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for the verification-grant gate and policy gate in ValidateOperationCommandHandler
/// (T7, T11a, spec 06 §6.1).
/// </summary>
[Trait("Category", "Unit")]
public class ValidateUserActionGateTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<IVerificationService> _verificationService = new();
    private readonly Mock<IValidationPolicy> _validationPolicy = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBookingRepository> _bookingRepo = new();

    private ValidateOperationCommandHandler CreateHandler() => new(
        _opRepo.Object,
        _stepRepo.Object,
        _verificationService.Object,
        _validationPolicy.Object,
        _outboxRepo.Object,
        _auditRepo.Object,
        _bookingRepo.Object,
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

    public ValidateUserActionGateTests()
    {
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
        // Default: all transitions succeed.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        // Default: policy passes (null = valid).
        _validationPolicy.Setup(p => p.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Fact]
    public async Task Handle_UserActionRequired_TransitionsToAwaitingUserAction_ReturnsWait()
    {
        var tenantId = "t-test";
        var operationId = "op-ua";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)   // outer guard check
            .ReturnsAsync(accepted)   // inner check (start of txn)
            .ReturnsAsync(validating); // after Accepted→Validating, fresh read for the gate

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = CreateHandler();
        var result = await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, validating.Version,
            OperationStatus.Validating, OperationStatus.AwaitingUserAction,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UserActionRequired_EmitsUserActionRequiredOutbox()
    {
        var tenantId = "t-test";
        var operationId = "op-ua-notif";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)
            .ReturnsAsync(accepted)
            .ReturnsAsync(validating);

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = CreateHandler();
        await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.user_action_required.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UserActionNotRequired_TransitionsToReservingCapacity_ReturnsContinue()
    {
        var tenantId = "t-test";
        var operationId = "op-no-ua";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)
            .ReturnsAsync(accepted)
            .ReturnsAsync(validating);

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = CreateHandler();
        var result = await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, validating.Version,
            OperationStatus.Validating, OperationStatus.ReservingCapacity,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        // Must NOT transition to AwaitingUserAction.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            OperationStatus.Validating, OperationStatus.AwaitingUserAction,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Held_RoutesToRequestPaymentCommand_InAdvanceDriver()
    {
        // This is a driver-routing concern verified in AdvanceBookingOperationCommandHandlerTests;
        // here we just confirm the validate handler does not interfere with Held status.
        var tenantId = "t-test";
        var operationId = "op-held";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Held));

        _verificationService.Setup(v => v.RequiresUserActionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = CreateHandler();
        var result = await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        // Validate handler treats non-Accepted/Validating as "already advanced past this step".
        Assert.Equal(StepOutcome.Continue, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── T11a: policy gate (Validating→Rejected) ─────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PolicyRejects_TransitionsToRejected_ReturnsTerminal()
    {
        var tenantId = "t-test";
        var operationId = "op-policy-reject";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)
            .ReturnsAsync(accepted)
            .ReturnsAsync(validating);

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _validationPolicy.Setup(p => p.CheckAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("underage_guest");

        var handler = CreateHandler();
        var result = await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, validating.Version,
            OperationStatus.Validating, OperationStatus.Rejected,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PolicyRejects_EmitsRejectedNotificationAndAudit()
    {
        var tenantId = "t-test";
        var operationId = "op-policy-notif";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)
            .ReturnsAsync(accepted)
            .ReturnsAsync(validating);

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _validationPolicy.Setup(p => p.CheckAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("blocked_by_policy");

        var handler = CreateHandler();
        await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.rejected.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.rejected"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PolicyPasses_DoesNotTransitionToRejected()
    {
        var tenantId = "t-test";
        var operationId = "op-policy-pass";
        var accepted = MakeOp(tenantId, operationId, OperationStatus.Accepted, version: 0);
        var validating = MakeOp(tenantId, operationId, OperationStatus.Validating, version: 1);

        _opRepo.SetupSequence(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted)
            .ReturnsAsync(accepted)
            .ReturnsAsync(validating);

        _verificationService.Setup(v => v.RequiresUserActionAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Default mock returns null (valid) — no override needed.

        var handler = CreateHandler();
        var result = await handler.Handle(new ValidateOperationCommand(tenantId, operationId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            OperationStatus.Validating, OperationStatus.Rejected,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
