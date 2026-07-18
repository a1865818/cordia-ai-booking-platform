using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.ReserveItems;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (Phase 3) — ReserveItems is the saga's items-first step. On success it advances
/// ReservingItems → CommittingBooking and triggers the spec-06 engine
/// (<c>booking.command.requested.v1</c>), which owns capacity/payment/create. On Unavailable it
/// advances ReservingItems → CompensatingItems (the saga holds no capacity of its own — booking is
/// delegated to 06).
/// </summary>
[Trait("Category", "Unit")]
public class ReservePreorderItemsHandlerTests
{
    private const string ValidReference =
        "{\"catalog_version\":\"menu_1\",\"confirmation_id\":\"conf_1\"," +
        "\"lines\":[{\"item_id\":\"burger\",\"modifier_ids\":[],\"quantity\":1," +
        "\"captured_price_amount_minor\":1500,\"captured_price_currency\":\"AUD\"}]}";

    private readonly Mock<IPreorderSagaRepository> _saga = new();
    private readonly Mock<IPreorderItemReservation> _items = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();

    private readonly List<OutboxMessage> _emitted = new();
    private (PreorderSagaState From, PreorderSagaState To)? _transition;

    public ReservePreorderItemsHandlerTests()
    {
        _saga.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreorderSaga
            {
                TenantId = "t_1",
                SagaId = "pos_1",
                OperationId = "op_1",
                SagaState = PreorderSagaState.ReservingItems,
                PreorderReference = ValidReference,
                Version = 3,
            });
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

    private ReservePreorderItemsCommandHandler CreateHandler() =>
        new(_saga.Object, _items.Object, _outbox.Object, _audit.Object, _jobs.Object,
            NullLogger<ReservePreorderItemsCommandHandler>.Instance);

    private static ReservePreorderItemsCommand Cmd() => new("t_1", "msg_1", "corr_1", "pos_1", "op_1");

    [Fact]
    public async Task Reserved_Advances_To_CommittingBooking_And_Triggers_06()
    {
        _items.Setup(i => i.ReserveAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PreorderReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ItemReservationStatus.Reserved);

        await CreateHandler().Handle(Cmd(), CancellationToken.None);

        Assert.Equal((PreorderSagaState.ReservingItems, PreorderSagaState.CommittingBooking), _transition);
        var msg = Assert.Single(_emitted);
        Assert.Equal("booking.command.requested.v1", msg.MessageType);
        Assert.Equal("op_1", msg.OperationId);
    }

    [Fact]
    public async Task Unavailable_Advances_To_CompensatingItems()
    {
        _items.Setup(i => i.ReserveAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PreorderReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ItemReservationStatus.Unavailable);

        await CreateHandler().Handle(Cmd(), CancellationToken.None);

        Assert.Equal((PreorderSagaState.ReservingItems, PreorderSagaState.CompensatingItems), _transition);
        var msg = Assert.Single(_emitted);
        Assert.Equal(PreorderSagaMessages.CompensateItems, msg.MessageType);
    }
}
