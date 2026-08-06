using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Enums;
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
    private readonly Mock<IConversationSessionRepository> _sessions = new();

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
        _intent.Setup(r => r.AddConfirmationAsync(It.IsAny<IntentConfirmation>(), It.IsAny<CancellationToken>()))
            .Callback<IntentConfirmation, CancellationToken>((cf, _) => _capturedConfirmation = cf)
            .Returns(Task.CompletedTask);
        _ops.Setup(r => r.AddAsync(It.IsAny<BookingOperation>(), It.IsAny<CancellationToken>()))
            .Callback<BookingOperation, CancellationToken>((op, _) => _capturedOp = op)
            .Returns(Task.CompletedTask);
        _outbox.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((m, _) => _capturedOutbox.Add(m))
            .Returns(Task.CompletedTask);
        _audit.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sessions.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationSession?)null);
    }

    private readonly Mock<IVerificationGrantRepository> _grants = new();

    // Every payload in this suite is a CREATE, so mutation authorization never engages — these
    // are here to satisfy the constructor, not to be exercised.
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<Cordia.Application.Common.Authorization.IGrantAuthorizationService> _grantAuth = new();
    private IntentConfirmation? _capturedConfirmation;

    private SubmitConfirmedIntentCommandHandler CreateHandler() =>
        new(_hasher.Object, _uow.Object, _idem.Object, _intent.Object, _ops.Object, _outbox.Object, _audit.Object,
            new Cordia.Infrastructure.Observability.Metrics(), _sessions.Object, _grants.Object,
            _bookings.Object, _grantAuth.Object);

    // ------------------------------------------------------------------
    // R15 — the assurance level is the SERVER's, not the caller's.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheGrantsRecordedAssuranceBeatsWhateverTheCallerAsserted()
    {
        // The caller understates itself. The platform's own grant says the customer was
        // identity-verified, and the platform's record is the one that counts — otherwise a
        // genuinely verified customer is recorded as unverified and later step-up gates
        // re-challenge them for no reason.
        _grants.Setup(g => g.GetAssuranceLevelAsync(
                It.IsAny<string>(), "grant_abc", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VerificationLevel.IdentityVerified);

        await CreateHandler().Handle(
            BuildCommand(identity: "grant_abc", assertedLevel: "unverified"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(_capturedConfirmation);
        Assert.Equal(VerificationLevel.IdentityVerified, _capturedConfirmation.VerificationLevel);
    }

    [Fact]
    public async Task AWellFormedOverclaimIsOverriddenByTheGrant()
    {
        // The converse, and the one that matters for trust: a lane that verified nobody cannot
        // record high_assurance just by saying so.
        _grants.Setup(g => g.GetAssuranceLevelAsync(
                It.IsAny<string>(), "grant_abc", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VerificationLevel.ChannelVerified);

        await CreateHandler().Handle(
            BuildCommand(identity: "grant_abc", assertedLevel: "high_assurance"),
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationLevel.ChannelVerified, _capturedConfirmation!.VerificationLevel);
    }

    [Fact]
    public async Task AnExpiredOrUnknownGrantFallsBackToTheAssertedLevel()
    {
        // Null from the repository means "no unexpired grant with that id". There is nothing
        // authoritative to use, so the (now vocabulary-validated) asserted value stands.
        _grants.Setup(g => g.GetAssuranceLevelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VerificationLevel?)null);

        await CreateHandler().Handle(
            BuildCommand(identity: "grant_gone", assertedLevel: "channel_verified"),
            TestContext.Current.CancellationToken);

        Assert.Equal(VerificationLevel.ChannelVerified, _capturedConfirmation!.VerificationLevel);
    }

    [Fact]
    public async Task AnOperatorIdentityIsNotLookedUpAtAll()
    {
        // The console sends an operator id, never a grant id. A query that can never match must
        // not sit on the p95<300ms acceptance path (spec 04 §10.3).
        await CreateHandler().Handle(
            BuildCommand(identity: "operator:console", assertedLevel: "unverified"),
            TestContext.Current.CancellationToken);

        _grants.Verify(g => g.GetAssuranceLevelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(VerificationLevel.Unverified, _capturedConfirmation!.VerificationLevel);
    }

    /// <summary>
    /// A create cannot borrow a stranger's grant to record a higher assurance than it earned.
    ///
    /// <para>There is no authorization gate on a create — nothing existing is being changed — so
    /// the level used to come from an unscoped `tenant + grant_id + unexpired` lookup, and
    /// `confirmation.identity` is caller-controlled. Naming any live grant in the tenant recorded
    /// its level. When the payload says who the booking is for, that is the binding: a grant issued
    /// to someone else attests nothing about this customer.</para>
    /// </summary>
    [Fact]
    public async Task ACreateDoesNotTakeItsAssuranceFromAGrantIssuedToSomeoneElse()
    {
        // Scoped lookup finds nothing: the grant is not this customer's.
        _grants.Setup(g => g.GetAssuranceLevelForCustomerAsync(
                It.IsAny<string>(), "grant_of_someone_else", "cust_9",
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VerificationLevel?)null);

        var command = BuildCommand(identity: "grant_of_someone_else", assertedLevel: "unverified");
        command.Request.Revision.CanonicalPayload.Customer = new CustomerRef { CustomerId = "cust_9" };

        await CreateHandler().Handle(command, TestContext.Current.CancellationToken);

        Assert.Equal(VerificationLevel.Unverified, _capturedConfirmation!.VerificationLevel);

        // And the UNSCOPED read is not consulted at all once there is a customer to bind to.
        _grants.Verify(g => g.GetAssuranceLevelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The converse: a grant that IS this customer's still supplies the level.</summary>
    [Fact]
    public async Task ACreateStillTakesItsAssuranceFromTheCustomersOwnGrant()
    {
        _grants.Setup(g => g.GetAssuranceLevelForCustomerAsync(
                It.IsAny<string>(), "grant_abc", "cust_9",
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VerificationLevel.IdentityVerified);

        var command = BuildCommand(identity: "grant_abc", assertedLevel: "unverified");
        command.Request.Revision.CanonicalPayload.Customer = new CustomerRef { CustomerId = "cust_9" };

        await CreateHandler().Handle(command, TestContext.Current.CancellationToken);

        Assert.Equal(VerificationLevel.IdentityVerified, _capturedConfirmation!.VerificationLevel);
    }

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

    /// <summary>MakeCommand with the confirmation's identity and asserted level overridden.</summary>
    private static SubmitConfirmedIntentCommand BuildCommand(string identity, string assertedLevel)
    {
        var command = MakeCommand(null);
        command.Request.Confirmation.Identity = identity;
        command.Request.Confirmation.VerificationLevel = assertedLevel;
        return command;
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
