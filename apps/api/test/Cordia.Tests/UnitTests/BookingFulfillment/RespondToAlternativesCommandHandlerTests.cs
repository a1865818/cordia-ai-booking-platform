using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="RespondToAlternativesCommandHandler"/> (spec 06 §6.1, §11, T11a).
/// </summary>
[Trait("Category", "Unit")]
public class RespondToAlternativesCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private RespondToAlternativesCommandHandler CreateHandler() => new(
        _opRepo.Object,
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

    public RespondToAlternativesCommandHandlerTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    // ── Waitlist path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waitlist_TransitionsToWaitlisted_EmitsNotificationAndAudit()
    {
        var tenantId = "t-test";
        var operationId = "op-wl";
        var op = MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 2);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        var handler = CreateHandler();
        await handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.AlternativesRequired, OperationStatus.Waitlisted,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.waitlisted.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.waitlisted"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Decline path ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_TransitionsToFailedFinal_EmitsNotificationAndAudit()
    {
        var tenantId = "t-test";
        var operationId = "op-decline";
        var op = MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 3);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        var handler = CreateHandler();
        await handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline), CancellationToken.None);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.AlternativesRequired, OperationStatus.FailedFinal,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.booking_failed.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.failed_final"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Guard: wrong status ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OpNotInAlternativesRequired_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-wrong-status";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Accepted));

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None));
    }

    // ── Guard: not found ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OpNotFound_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-notfound";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline), CancellationToken.None));
    }

    // ── Guard: tenant mismatch ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_TenantMismatch_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-mismatch";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp("different-tenant", operationId, OperationStatus.AlternativesRequired));

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None));
    }
}
