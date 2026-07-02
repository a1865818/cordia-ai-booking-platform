using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.Reconcile;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §21.4 / §8 FM#10 — a saga stuck in <c>CommittingPreorder</c> (booking committed,
/// pre-order bind not landing) must not be re-driven forever by step timeouts + recovery sweeps.
/// Past <see cref="ReconcilePreorderSagaCommandHandler.CommitStuckEscalationAfter"/> the reconcile
/// escalates <c>CommittingPreorder → CompensatingBooking</c> (whose handler parks ManualReview for
/// the operator+customer resolution). This is the producer that makes ManualReview reachable
/// (review PR #65, finding 5).
/// </summary>
[Trait("Category", "Unit")]
public class PreorderReconcileEscalationTests
{
    private const string TenantId = "t_1";
    private const string SagaId = "pos_1";
    private const string OperationId = "op_1";

    private readonly Mock<IPreorderSagaRepository> _sagaRepo = new();
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();

    private ReconcilePreorderSagaCommandHandler CreateHandler() => new(
        _sagaRepo.Object, _opRepo.Object, _outbox.Object, _audit.Object,
        NullLogger<ReconcilePreorderSagaCommandHandler>.Instance);

    private static PreorderSaga Saga(PreorderSagaState state, TimeSpan age) => new()
    {
        TenantId = TenantId,
        SagaId = SagaId,
        OperationId = OperationId,
        SagaState = state,
        UpdatedAt = DateTimeOffset.UtcNow - age,
        Version = 2,
    };

    private ReconcilePreorderSagaCommand Command() =>
        new(TenantId, $"msg_{Guid.NewGuid():N}", null, SagaId, OperationId);

    [Fact]
    public async Task CommittingPreorder_StuckPastWindow_EscalatesToCompensatingBooking()
    {
        var stuckFor = ReconcilePreorderSagaCommandHandler.CommitStuckEscalationAfter + TimeSpan.FromMinutes(1);
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingPreorder, stuckFor));
        _sagaRepo.Setup(r => r.TryAdvanceStateAsync(
                TenantId, SagaId, It.IsAny<long>(),
                PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking,
                It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(Command(), CancellationToken.None);

        // Escalated (state advance won) + the CompensateBooking step is enqueued — its handler
        // parks the saga in ManualReview and emits the neutral in-review notice (Inv-8).
        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            TenantId, SagaId, It.IsAny<long>(),
            PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking,
            It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.CompensateBooking),
            It.IsAny<CancellationToken>()), Times.Once);
        // The stuck step is NOT re-driven once escalated.
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.CommitPreorder),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CommittingPreorder_WithinWindow_ResumesCommitStep_NoEscalation()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingPreorder, TimeSpan.FromMinutes(1)));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        // Still inside the window — reconcile re-drives the idempotent CommitPreorder step.
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.CommitPreorder),
            It.IsAny<CancellationToken>()), Times.Once);
        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CommittingBooking_OpParkedInManualReview_Settles_WithoutRenudging()
    {
        // Review 2026-07-03 C16: a 06 op in ManualReview is human-owned and the 06 driver no-ops
        // for it — re-nudging booking.command.requested.v1 every sweep tick spins forever. The
        // reconcile must settle; ResolveManualReview signals the saga when the operator resolves.
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingBooking, TimeSpan.FromHours(2)));
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingOperation
            {
                TenantId = TenantId,
                OperationId = OperationId,
                IntentId = "int_1",
                IntentRevision = 1,
                Status = OperationStatus.ManualReview,
                IsPreorder = true,
                Version = 4,
            });

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CommittingPreorder_StuckPastWindow_LostAdvanceRace_DoesNotEnqueueCompensate()
    {
        var stuckFor = ReconcilePreorderSagaCommandHandler.CommitStuckEscalationAfter + TimeSpan.FromMinutes(1);
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingPreorder, stuckFor));
        _sagaRepo.Setup(r => r.TryAdvanceStateAsync(
                TenantId, SagaId, It.IsAny<long>(),
                PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking,
                It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // a concurrent commit/duplicate reconcile won

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _outbox.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
