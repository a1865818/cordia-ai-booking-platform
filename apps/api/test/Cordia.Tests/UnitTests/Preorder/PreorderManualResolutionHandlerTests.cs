using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.ManualResolution;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §21.4 — an operator + customer resolve a pre-order parked in <c>ManualReview</c>: convert
/// it to a standalone order (→ Completed, marked standalone, audited with both parties) or cancel it
/// (→ FailedFinal). These tests pin the guarded advance, the audit evidence, idempotency on an
/// already-resolved saga, and the 404/409 rejections.
/// </summary>
[Trait("Category", "Unit")]
public class PreorderManualResolutionHandlerTests
{
    private const string TenantId = "t_1";
    private const string SagaId = "pos_1";
    private const string OperationId = "op_1";
    private const string Operator = "op_alice";
    private const string CustomerEvidence = "cust_agree_1";

    private readonly Mock<IPreorderSagaRepository> _sagaRepo = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public PreorderManualResolutionHandlerTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
        _outbox.Setup(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private ConvertPreorderToStandaloneCommandHandler Convert() => new(
        _sagaRepo.Object, _outbox.Object, _audit.Object, _uow.Object,
        NullLogger<ConvertPreorderToStandaloneCommandHandler>.Instance);

    private CancelPreorderManualReviewCommandHandler Cancel() => new(
        _sagaRepo.Object, _outbox.Object, _audit.Object, _uow.Object,
        NullLogger<CancelPreorderManualReviewCommandHandler>.Instance);

    private void SetupAdvance(PreorderSagaState from, PreorderSagaState to, int rows) =>
        _sagaRepo.Setup(r => r.TryAdvanceStateAsync(
                TenantId, SagaId, It.IsAny<long>(), from, to, It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

    private static PreorderSaga Saga(PreorderSagaState state) => new()
    {
        TenantId = TenantId,
        SagaId = SagaId,
        OperationId = OperationId,
        SagaState = state,
        Version = 3,
    };

    // ── Convert ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_FromManualReview_Advances_SetsStandalone_Emits_And_Audits()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        SetupAdvance(PreorderSagaState.ManualReview, PreorderSagaState.Completed, 1);

        await Convert().Handle(
            new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, CustomerEvidence), CancellationToken.None);

        // Advances ManualReview → Completed, marking the saga standalone (§21.4).
        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            TenantId, SagaId, It.IsAny<long>(),
            PreorderSagaState.ManualReview, PreorderSagaState.Completed, It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), true,
            It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.BookingConfirmedNotification),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "preorder.standalone_converted" && e.Actor == Operator),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Convert_AlreadyCompleted_IsIdempotent_NoAdvance()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.Completed));

        await Convert().Handle(
            new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, CustomerEvidence), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Convert_NotManualReview_Throws_409()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingBooking));

        await Assert.ThrowsAsync<InvalidPreorderSagaTransitionException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, CustomerEvidence), CancellationToken.None));
    }

    [Fact]
    public async Task Convert_Unknown_Throws_404()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreorderSaga?)null);

        await Assert.ThrowsAsync<PreorderSagaNotFoundException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, CustomerEvidence), CancellationToken.None));
    }

    // ── Cancel ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_FromManualReview_Advances_FailedFinal_Emits_And_Audits()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        SetupAdvance(PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal, 1);

        await Cancel().Handle(
            new CancelPreorderManualReviewCommand(TenantId, SagaId, Operator, Reason: "customer_declined"), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            TenantId, SagaId, It.IsAny<long>(),
            PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal, It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaSupport.OutcomeFailedEventType),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "preorder.manual_review_cancelled"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_AlreadyFailedFinal_IsIdempotent_NoAdvance()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.FailedFinal));

        await Cancel().Handle(
            new CancelPreorderManualReviewCommand(TenantId, SagaId, Operator, Reason: null), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
