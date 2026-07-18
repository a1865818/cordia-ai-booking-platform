using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Compensate;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="CompensateOperationCommandHandler"/> (spec 06 §9 FM#12, §11, §15.5).
///
/// Covers:
/// - providerRef present + cancel acknowledged → FailedFinal transition + booking_failed outbox.
/// - providerRef present + cancel ambiguous → ManualReviewEscalator.EscalateAsync called.
/// - providerRef null → FailedFinal with CancelBooking NEVER called.
/// </summary>
[Trait("Category", "Unit")]
public class CompensateOperationTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<IWorkflowAttemptRepository> _attemptRepo = new();
    private readonly Mock<IBookingProviderAdapter> _adapter = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<ICapacityReservation> _capacityReservation = new();

    private ManualReviewEscalator CreateEscalator() =>
        new(_opRepo.Object, _workItemRepo.Object, _outboxRepo.Object, _auditRepo.Object);

    private CompensateOperationCommandHandler CreateHandler() =>
        new(_opRepo.Object, _stepRepo.Object, _attemptRepo.Object,
            _adapter.Object, _outboxRepo.Object, _auditRepo.Object,
            CreateEscalator(), _uow.Object, _capacityReservation.Object, new Metrics());

    private static BookingOperation MakeOp(
        string tenantId, string operationId, OperationStatus status, long version = 0,
        DateTimeOffset? cancellationRequestedAt = null) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
            CancellationRequestedAt = cancellationRequestedAt,
        };

    private static WorkflowAttempt MakeAttempt(
        string tenantId, string operationId, AttemptOutcome outcome,
        string? providerRef = null) =>
        new()
        {
            TenantId = tenantId,
            AttemptId = "att-1",
            OperationId = operationId,
            WorkflowStep = "finalize",
            ProviderIdempotencyKey = "pik_test",
            ProviderName = "stub",
            ProviderAccount = "default",
            RequestHash = "h",
            AttemptNumber = 1,
            Outcome = outcome,
            ProviderRef = providerRef,
            RequestMeta = "{}",
            ResponseMeta = "{}",
        };

    public CompensateOperationTests()
    {
        _adapter.Setup(a => a.ProviderName).Returns("stub");

        // UoW executes work immediately by default.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Step repo: no existing compensate step (so it gets created).
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(
                It.IsAny<string>(), It.IsAny<string>(), "compensate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);

        _stepRepo.Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<WorkflowStepStatus>(), It.IsAny<WorkflowStepStatus>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    // ── Guard: non-CompensationRequired op → Continue ────────────────────────────────────────

    [Fact]
    public async Task Handle_OpNotCompensationRequired_ReturnsContinue()
    {
        var (tenantId, opId) = ("t-1", "op-1");
        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Finalizing));

        var result = await CreateHandler().Handle(
            new CompensateOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _adapter.Verify(a => a.CancelBookingAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Guard: null op → Wait ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_NullOp_ReturnsWait()
    {
        _opRepo.Setup(r => r.GetAsync("op-null", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var result = await CreateHandler().Handle(
            new CompensateOperationCommand("t-x", "op-null"), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
    }

    // ── providerRef present + cancel acknowledged → FailedFinal + outbox ─────────────────────

    [Fact]
    public async Task Handle_ProviderRefPresent_CancelAcknowledged_TransitionsToFailedFinal()
    {
        var (tenantId, opId) = ("t-comp", "op-comp");
        const string providerRef = "ext_ref_to_cancel";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.CompensationRequired, version: 2));
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Succeeded, providerRef: providerRef));

        // CancelBookingAsync returns Created (cancel acknowledged).
        _adapter.Setup(a => a.CancelBookingAsync("default", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));

        // TryTransitionStatusAsync: CompensationRequired → FailedFinal succeeds.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new CompensateOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        // Verify FailedFinal transition was requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify notification.booking_failed.v1 outbox row added.
        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m =>
                m.TenantId == tenantId &&
                m.OperationId == opId &&
                m.MessageType == "notification.booking_failed.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── providerRef present + cancel ambiguous → ManualReviewEscalator called ────────────────

    [Fact]
    public async Task Handle_ProviderRefPresent_CancelAmbiguous_EscalatesToManualReview()
    {
        var (tenantId, opId) = ("t-amb", "op-amb");
        const string providerRef = "ext_ref_ambiguous";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.CompensationRequired, version: 3));
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Succeeded, providerRef: providerRef));

        // CancelBookingAsync returns Ambiguous.
        _adapter.Setup(a => a.CancelBookingAsync("default", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Ambiguous));

        // ManualReviewEscalator calls TryTransitionStatusAsync internally.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.CompensationRequired, OperationStatus.ManualReview,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _workItemRepo.Setup(r => r.AddAsync(It.IsAny<OperationWorkItem>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(
            new CompensateOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);

        // ManualReview transition was requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.CompensationRequired, OperationStatus.ManualReview,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // FailedFinal was NOT requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── providerRef null → FailedFinal, CancelBooking NEVER called ──────────────────────────

    [Fact]
    public async Task Handle_ProviderRefNull_FailedFinal_CancelNeverCalled()
    {
        var (tenantId, opId) = ("t-null", "op-null-ref");

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.CompensationRequired, version: 1));
        // No finalize attempt with a ProviderRef.
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowAttempt?)null);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new CompensateOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        // CancelBooking was NEVER called (nothing to cancel).
        _adapter.Verify(a => a.CancelBookingAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // FailedFinal transition was requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // booking_failed outbox row added.
        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m =>
                m.TenantId == tenantId &&
                m.OperationId == opId &&
                m.MessageType == "notification.booking_failed.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
