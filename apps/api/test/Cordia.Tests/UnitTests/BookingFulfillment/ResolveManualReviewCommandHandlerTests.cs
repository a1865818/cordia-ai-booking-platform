using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ResolveManualReviewCommandHandler"/>.
/// Verifies: Booked (requires ProviderRef) → Booked + notification.booking_confirmed.v1 + work item closed;
/// FailedFinal → FailedFinal + notification.booking_failed.v1 + work item closed;
/// Booked without ProviderRef → OperationConflictException;
/// non-ManualReview → OperationConflictException; not-found → OperationConflictException.
/// </summary>
[Trait("Category", "Unit")]
public class ResolveManualReviewCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ResolveManualReviewCommandHandlerTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private ResolveManualReviewCommandHandler CreateHandler() =>
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

    // ── Booked: requires ProviderRef ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Booked_RequiresProviderRef_TransitionsAndEmitsConfirmedNotification()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string replayId = "rpl_xyz";
        const string providerRef = "ext_ref_123";
        const string actor = "ops";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 4);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 4,
                OperationStatus.ManualReview, OperationStatus.Booked,
                "manual_resolved_booked", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationWorkItem?)null); // no open work item — still should succeed

        OutboxMessage? capturedMsg = null;
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((msg, _) => capturedMsg = msg)
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        await handler.Handle(
            new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked, providerRef, replayId, actor, "rc_test"),
            CancellationToken.None);

        // notification.booking_confirmed.v1
        Assert.NotNull(capturedMsg);
        Assert.Equal("notification.booking_confirmed.v1", capturedMsg.MessageType);
        Assert.Contains(providerRef, capturedMsg.Payload);

        // audit
        Assert.NotNull(capturedAudit);
        Assert.Equal("operation.manual_resolved", capturedAudit.EventType);
        Assert.Contains(replayId, capturedAudit.Metadata);
        Assert.Contains("booked", capturedAudit.Metadata);
    }

    [Fact]
    public async Task Handle_Booked_MissingProviderRef_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked,
                    ProviderRef: null, "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Handle_Booked_EmptyProviderRef_ThrowsOperationConflictException()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.Booked,
                    ProviderRef: "   ", "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }

    // ── FailedFinal: no ProviderRef required ─────────────────────────────────────────────────

    [Fact]
    public async Task Handle_FailedFinal_TransitionsAndEmitsFailedNotification_ClosesWorkItem()
    {
        const string tenantId = "t1";
        const string operationId = "op-1";
        const string replayId = "rpl_abc";
        const string actor = "ops";

        var op = MakeOp(tenantId, operationId, OperationStatus.ManualReview, version: 7);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, operationId, 7,
                OperationStatus.ManualReview, OperationStatus.FailedFinal,
                "manual_resolved_failed", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var workItem = new OperationWorkItem
        {
            TenantId = tenantId,
            WorkItemId = "wi-1",
            OperationId = operationId,
            State = "open",
            Version = 2,
        };
        _workItemRepo.Setup(r => r.GetOpenByOperationAsync(tenantId, operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItem);
        _workItemRepo.Setup(r => r.TryUpdateStateAsync(
                tenantId, "wi-1", 2, "open", "resolved", actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        OutboxMessage? capturedMsg = null;
        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((msg, _) => capturedMsg = msg)
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        await handler.Handle(
            new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                ProviderRef: null, replayId, actor, "rc_test"),
            CancellationToken.None);

        // notification.booking_failed.v1
        Assert.NotNull(capturedMsg);
        Assert.Equal("notification.booking_failed.v1", capturedMsg.MessageType);

        // audit
        Assert.NotNull(capturedAudit);
        Assert.Equal("operation.manual_resolved", capturedAudit.EventType);
        Assert.Contains(replayId, capturedAudit.Metadata);
        Assert.Contains("failedfinal", capturedAudit.Metadata);

        // work item closed
        _workItemRepo.Verify(r => r.TryUpdateStateAsync(
            tenantId, "wi-1", 2, "open", "resolved", actor, It.IsAny<CancellationToken>()), Times.Once);
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
                new ResolveManualReviewCommand("t1", "op-x", ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }

    // ── Non-ManualReview → OperationConflictException ────────────────────────────────────────

    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.FailedFinal)]
    public async Task Handle_NonManualReview_ThrowsOperationConflictException(OperationStatus status)
    {
        const string tenantId = "t1";
        const string operationId = "op-1";

        var op = MakeOp(tenantId, operationId, status);
        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test"),
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
                tenantId, operationId, It.IsAny<long>(),
                OperationStatus.ManualReview, It.IsAny<OperationStatus>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // lost race

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(
                new ResolveManualReviewCommand(tenantId, operationId, ManualReviewResolution.FailedFinal,
                    null, "rpl_1", "ops", "rc_test"),
                CancellationToken.None));
    }
}
