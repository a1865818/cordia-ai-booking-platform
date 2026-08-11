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
            // EXPLICIT. `OperationTypeEnum.Cancel` is the zero value, so leaving this unset made
            // every one of these tests silently exercise a cancellation while reading as a
            // generic operation — which matters now that cancel/modify/reschedule take a
            // different manual-recovery path from a create.
            OperationType = Domain.Generated.OperationTypeEnum.Create,
        };

    /// <summary>
    /// A mutation cannot be resumed, because resuming it is a loop rather than a recovery.
    ///
    /// <para>Resume means ManualReview → Reconciling, and the reconcile handler sends
    /// cancel/modify/reschedule straight back to ManualReview on sight — its lookup is
    /// create-shaped and cannot establish whether a cancellation landed or an amendment applied.
    /// The operator would press Resume and watch the row return unchanged, forever. Refused with a
    /// message naming the door that does work.</para>
    /// </summary>
    [Theory]
    [InlineData(Domain.Generated.OperationTypeEnum.Cancel)]
    [InlineData(Domain.Generated.OperationTypeEnum.Modify)]
    [InlineData(Domain.Generated.OperationTypeEnum.Reschedule)]
    public async Task Handle_MutationOperation_RefusesResumeInsteadOfLooping(
        Domain.Generated.OperationTypeEnum operationType)
    {
        const string tenantId = "t-resume-mut";
        const string operationId = "op-resume-mut";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 3);
        op.OperationType = operationType;
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new ResumeWorkflowStepCommand(tenantId, operationId, "rpl_mut", "ops", "rc_test", "operator"),
                CancellationToken.None));

        // It never left ManualReview, so there is nothing for the reconcile guard to bounce back.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            OperationStatus.ManualReview, OperationStatus.Reconciling,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
            new ResumeWorkflowStepCommand(tenantId, operationId, replayId, actor, "rc_test", "operator"),
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
                new ResumeWorkflowStepCommand("t1", "op-x", "rpl_1", "ops", "rc_test", "operator"),
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
                new ResumeWorkflowStepCommand(tenantId, operationId, "rpl_1", "ops", "rc_test", "operator"),
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
                new ResumeWorkflowStepCommand(tenantId, operationId, "rpl_1", "ops", "rc_test", "operator"),
                CancellationToken.None));
    }
}
