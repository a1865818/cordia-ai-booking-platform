using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.BookingSignal;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (Phase 3) — the 06→saga booking-outcome signal consumers close the delegation loop:
/// <c>booking_committed</c> advances CommittingBooking → CommittingPreorder (+ enqueue commit_preorder),
/// <c>booking_failed</c> advances CommittingBooking → CompensatingItems (+ enqueue compensate_items).
/// Both resolve the saga by operation id and are idempotent (settle when not in CommittingBooking).
/// </summary>
[Trait("Category", "Unit")]
public class PreorderBookingSignalHandlerTests
{
    private readonly Mock<IPreorderSagaRepository> _saga = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();

    private readonly List<OutboxMessage> _emitted = new();
    private (PreorderSagaState From, PreorderSagaState To)? _transition;

    public PreorderBookingSignalHandlerTests()
    {
        _saga.Setup(r => r.TryAdvanceStateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, long _, PreorderSagaState from, PreorderSagaState to,
                       DateTimeOffset _, string _, string _, string _, bool? _, CancellationToken _) => _transition = (from, to))
            .ReturnsAsync(1);
        _outbox.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((m, _) => _emitted.Add(m))
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private void SagaInState(PreorderSagaState state) =>
        _saga.Setup(r => r.GetByOperationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreorderSaga
            {
                TenantId = "t_1",
                SagaId = "pos_1",
                OperationId = "op_1",
                SagaState = state,
                Version = 5,
            });

    [Fact]
    public async Task Committed_Advances_To_CommittingPreorder_And_Enqueues_Commit()
    {
        SagaInState(PreorderSagaState.CommittingBooking);
        var handler = new PreorderBookingCommittedCommandHandler(
            _saga.Object, _outbox.Object, _audit.Object, _jobs.Object,
            NullLogger<PreorderBookingCommittedCommandHandler>.Instance);

        await handler.Handle(new PreorderBookingCommittedCommand("t_1", "msg_1", "corr_1", "op_1"), CancellationToken.None);

        Assert.Equal((PreorderSagaState.CommittingBooking, PreorderSagaState.CommittingPreorder), _transition);
        Assert.Equal(PreorderSagaMessages.CommitPreorder, Assert.Single(_emitted).MessageType);
    }

    [Fact]
    public async Task Failed_Advances_To_CompensatingItems_And_Enqueues_Compensate()
    {
        SagaInState(PreorderSagaState.CommittingBooking);
        var handler = new PreorderBookingFailedCommandHandler(
            _saga.Object, _outbox.Object, _audit.Object,
            NullLogger<PreorderBookingFailedCommandHandler>.Instance);

        await handler.Handle(new PreorderBookingFailedCommand("t_1", "msg_1", "corr_1", "op_1"), CancellationToken.None);

        Assert.Equal((PreorderSagaState.CommittingBooking, PreorderSagaState.CompensatingItems), _transition);
        Assert.Equal(PreorderSagaMessages.CompensateItems, Assert.Single(_emitted).MessageType);
    }

    [Fact]
    public async Task Committed_Settles_When_Not_In_CommittingBooking()
    {
        SagaInState(PreorderSagaState.Completed); // duplicate/late delivery
        var handler = new PreorderBookingCommittedCommandHandler(
            _saga.Object, _outbox.Object, _audit.Object, _jobs.Object,
            NullLogger<PreorderBookingCommittedCommandHandler>.Instance);

        await handler.Handle(new PreorderBookingCommittedCommand("t_1", "msg_1", "corr_1", "op_1"), CancellationToken.None);

        Assert.Null(_transition);
        Assert.Empty(_emitted);
    }
}
