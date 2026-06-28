using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for the NoCapacity routing in <see cref="ReserveCapacityCommandHandler"/>
/// (spec 06 §6.3, FM#8).
/// </summary>
[Trait("Category", "Unit")]
public class ReserveCapacityNoCapacityTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<ICapacityService> _capacityService = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ReserveCapacityNoCapacityTests()
    {
        // UoW executes work immediately.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private ReserveCapacityCommandHandler CreateHandler() =>
        new(_opRepo.Object, _stepRepo.Object, _capacityService.Object,
            _outboxRepo.Object, _auditRepo.Object, _uow.Object);

    private static BookingOperation MakeOp(string tenantId, string opId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = opId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
        };

    // ── NoCapacity → AlternativesRequired transition requested ──────────────────────────────

    [Fact]
    public async Task NoCapacity_RequestsAlternativesRequiredTransition()
    {
        var (tenantId, opId) = ("t-nocap", "op-nocap");

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity));

        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "reserve_capacity", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _stepRepo.Setup(r => r.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capacityService.Setup(c => c.TryReserveAsync(tenantId, opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CapacityResult.NoCapacity);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        // Returns Wait (AlternativesRequired requires external user input).
        Assert.Equal(StepOutcome.Wait, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── NoCapacity → notification outbox row added ────────────────────────────────────────────

    [Fact]
    public async Task NoCapacity_AddsNotificationOutboxRow()
    {
        var (tenantId, opId) = ("t-nocap-notif", "op-nocap-notif");

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity));

        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "reserve_capacity", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _stepRepo.Setup(r => r.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capacityService.Setup(c => c.TryReserveAsync(tenantId, opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CapacityResult.NoCapacity);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(
            new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m =>
                m.TenantId == tenantId &&
                m.OperationId == opId &&
                m.MessageType == "notification.alternatives_required.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Reserved → Held transition (not AlternativesRequired) ────────────────────────────────

    [Fact]
    public async Task Reserved_RequestsHeldTransition_NotAlternativesRequired()
    {
        var (tenantId, opId) = ("t-reserved", "op-reserved");

        _opRepo.SetupSequence(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.ReservingCapacity));

        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "reserve_capacity", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _stepRepo.Setup(r => r.AddAsync(It.IsAny<WorkflowStep>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capacityService.Setup(c => c.TryReserveAsync(tenantId, opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CapacityResult.Reserved);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.ReservingCapacity, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReserveCapacityCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // AlternativesRequired transition should NOT be requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
