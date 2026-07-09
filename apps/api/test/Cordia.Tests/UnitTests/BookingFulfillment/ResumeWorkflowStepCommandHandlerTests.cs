using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ResumeWorkflowStepCommandHandler"/>.
/// Verifies: ManualReview → Reconciling + continuation outbox + replay audit (Inv-15);
/// non-ManualReview → OperationConflictException; not-found → OperationConflictException.
/// </summary>
[Trait("Category", "Unit")]
public class ResumeWorkflowStepCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ResumeWorkflowStepCommandHandlerTests()
    {
        // UoW executes work immediately (same pattern as ReconcileOperationCommandHandlerTests).
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Default: no open work item.
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null);
    }

    private ResumeWorkflowStepCommandHandler CreateHandler() =>
        new(_opRepo.Object, _workItemRepo.Object, _outboxRepo.Object, new AuditWriter(_auditRepo.Object), _uow.Object);

    private static BookingOperation MakeOp(string tenantId, string operationId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
        };

    // ── Happy path: ManualReview → Reconciling + outbox + audit ─────────────────────────────

    [Fact]
    public async Task Handle_ManualReview_TransitionsToReconciling_EmitsOutboxAndAudit()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string replayId = "rpl_abc";
        const string actor = "ops";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 5);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 5,
                OperationStatus.ManualReview, OperationStatus.Reconciling,
                "manual_resume", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        OutboxMessage? capturedOutbox = null;
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((msg, _) => capturedOutbox = msg)
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        await handler.Handle(
            new ResumeWorkflowStepCommand(tenantId, operationId, replayId, actor, "rc_test"),
            CancellationToken.None);

        // Assert outbox: booking.command.requested.v1 continuation.
        Assert.NotNull(capturedOutbox);
        Assert.Equal("booking.command.requested.v1", capturedOutbox.MessageType);
        Assert.Equal(operationId, capturedOutbox.OperationId);
        Assert.Equal(tenantId, capturedOutbox.TenantId);
        Assert.Contains(operationId, capturedOutbox.Payload);

        // Assert audit: operation.replay with ReplayCommandId.
        Assert.NotNull(capturedAudit);
        Assert.Equal("operation.replay", capturedAudit.EventType);
        Assert.Equal(actor, capturedAudit.Actor);
        Assert.Contains(replayId, capturedAudit.Metadata);
        Assert.Contains("resume", capturedAudit.Metadata);
    }

    // ── Not-found → OperationConflictException ───────────────────────────────────────────────

    [Fact]
    public async Task Handle_OperationNotFound_ThrowsOperationConflictException()
    {
        _opRepo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResumeWorkflowStepCommand("t1", "op-x", "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }

    // ── Non-ManualReview → OperationConflictException ────────────────────────────────────────

    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Finalizing)]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Reconciling)]
    public async Task Handle_NonManualReview_ThrowsOperationConflictException(OperationStatus status)
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, status);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResumeWorkflowStepCommand(tenantId, operationId, "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }

    // ── Concurrent change (rows==0) → OperationConflictException ─────────────────────────────

    [Fact]
    public async Task Handle_ConcurrentChange_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 3);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 3,
                OperationStatus.ManualReview, OperationStatus.Reconciling,
                "manual_resume", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // concurrent winner already advanced the row

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResumeWorkflowStepCommand(tenantId, operationId, "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }
}
