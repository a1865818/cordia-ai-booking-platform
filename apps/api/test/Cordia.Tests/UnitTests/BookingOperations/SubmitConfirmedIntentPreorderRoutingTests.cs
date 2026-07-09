using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Spec 11 §5.3 (divergence #2) — the 04 acceptance handler routes a confirmed revision carrying a
/// non-empty <c>PreorderReference</c> to the pre-order saga (<c>preorder.commands.start.v1</c>) and
/// marks the operation <c>IsPreorder</c>, instead of emitting <c>booking.command.requested.v1</c>
/// straight to the 06 engine. A non-pre-order acceptance is unchanged
/// (<c>booking.command.requested.v1</c>, <c>IsPreorder = false</c>). This wires the saga's missing
/// producer; the saga then reserves items and delegates the whole booking to 06 (Phase 3).
/// </summary>
[Trait("Category", "Unit")]
public class SubmitConfirmedIntentPreorderRoutingTests
{
    private readonly Mock<ICanonicalRequestHasher> _hasher = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IIdempotencyRepository> _idem = new();
    private readonly Mock<IBookingIntentRepository> _intent = new();
    private readonly Mock<IBookingOperationRepository> _ops = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _audit = new();

    private BookingOperation? _capturedOp;
    private readonly List<OutboxMessage> _capturedOutbox = new();

    public SubmitConfirmedIntentPreorderRoutingTests()
    {
        // Fixed hash; the confirmation below carries the same value so the §8.8 check passes.
        _hasher.Setup(h => h.Hash(It.IsAny<JsonNode>())).Returns(new CanonicalHash("canon.v1", "H"));
        // UoW runs the work immediately (same pattern as the other handler unit tests).
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
        _idem.Setup(r => r.GetByKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        _idem.Setup(r => r.ElectCreatorAsync(It.IsAny<IdempotencyRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _intent.Setup(r => r.IntentExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _intent.Setup(r => r.AddIntentAsync(It.IsAny<BookingIntent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _intent.Setup(r => r.AddRevisionAsync(It.IsAny<BookingIntentRevision>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _intent.Setup(r => r.AddConfirmationAsync(It.IsAny<IntentConfirmation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ops.Setup(r => r.AddAsync(It.IsAny<BookingOperation>(), It.IsAny<CancellationToken>()))
            .Callback<BookingOperation, CancellationToken>((op, _) => _capturedOp = op)
            .Returns(Task.CompletedTask);
        _outbox.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((m, _) => _capturedOutbox.Add(m))
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private SubmitConfirmedIntentCommandHandler CreateHandler() =>
        new(_hasher.Object, _uow.Object, _idem.Object, _intent.Object, _ops.Object, _outbox.Object, _audit.Object);

    [Fact]
    public async Task Preorder_Routes_To_Saga_Start_And_Marks_Operation()
    {
        await CreateHandler().Handle(MakeCommand(SamplePreorder()), CancellationToken.None);

        Assert.NotNull(_capturedOp);
        Assert.True(_capturedOp!.IsPreorder);

        var msg = Assert.Single(_capturedOutbox);
        Assert.Equal(PreorderSagaMessages.Start, msg.MessageType); // preorder.commands.start.v1

        using var doc = JsonDocument.Parse(msg.Payload);
        Assert.Equal("op_1", doc.RootElement.GetProperty("operation_id").GetString());
        // The start payload no longer carries a standalone/slot flag — the saga never starts standalone
        // (booking delegated to 06; standalone is a later operator convert path, §21.4).
        Assert.False(doc.RootElement.TryGetProperty("standalone", out _));
        Assert.Equal(
            "menu_1",
            doc.RootElement.GetProperty("preorder_reference").GetProperty("catalog_version").GetString());
    }

    [Fact]
    public async Task NonPreorder_Routes_To_Booking_Requested_Unchanged()
    {
        await CreateHandler().Handle(MakeCommand(preorder: null), CancellationToken.None);

        Assert.NotNull(_capturedOp);
        Assert.False(_capturedOp!.IsPreorder);

        var msg = Assert.Single(_capturedOutbox);
        Assert.Equal("booking.command.requested.v1", msg.MessageType);
    }

    private static SubmitConfirmedIntentCommand MakeCommand(PreorderReference? preorder)
    {
        var payload = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = "svc_1",
            LocationId = "loc_1",
            Preorder = preorder!,
        };
        var revision = new IntentRevision
        {
            IntentId = "int_1",
            Revision = 1,
            CanonicalPayload = payload,
            SchemaVersion = "v1",
            TenantPolicyVersion = "v1",
            SourceSessionId = "sess_1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var confirmation = new Confirmation
        {
            ConfirmationId = "conf_1",
            IntentId = "int_1",
            IntentRevision = 1,
            IntentPayloadHash = "H",
            ConfirmationChannel = ChannelEnum.Email,
            Identity = "cust_1",
            ConfirmedAt = DateTimeOffset.UtcNow,
        };
        var request = new SubmitConfirmedIntentRequest
        {
            IdempotencyKey = "idem_1",
            OperationId = "op_1",
            IntentId = "int_1",
            IntentRevision = 1,
            Revision = revision,
            Confirmation = confirmation,
        };
        return new SubmitConfirmedIntentCommand("t_1", request);
    }

    private static PreorderReference SamplePreorder() => new()
    {
        CatalogVersion = "menu_1",
        ConfirmationId = "conf_1",
        Lines = new List<PreorderLineReference>
        {
            new()
            {
                ItemId = "burger",
                ModifierIds = new List<string>(),
                Quantity = 1,
                CapturedPriceAmountMinor = 1500,
                CapturedPriceCurrency = "AUD",
            },
        },
    };
}
