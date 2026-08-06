using System.Text.Json.Nodes;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Authorization of a MUTATION submitted through the acceptance front door (spec 08 §9.3/§12,
/// Inv-11).
///
/// <para>A reschedule can only be submitted here — <c>SubmitModificationInput</c> is
/// <c>additionalProperties: false</c> with no field for the new time, so the dedicated
/// manage-booking door cannot carry one. That makes this the door where the grant rules have to
/// hold, and the two properties under test are the ones it was missing: the grant is SPENT (every
/// dedicated door consumes; this one only read), and a denial that happens before the grant
/// service is reached still leaves an audit row.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SubmitConfirmedIntentMutationAuthorizationTests
{
    private const string TenantId = "t_1";
    private const string BookingId = "bk_1";
    private const string CustomerId = "cust_1";
    private const string GrantId = "grant_live";

    private readonly Mock<ICanonicalRequestHasher> _hasher = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IIdempotencyRepository> _idem = new();
    private readonly Mock<IBookingIntentRepository> _intent = new();
    private readonly Mock<IBookingOperationRepository> _ops = new();
    private readonly Mock<IOutboxRepository> _outbox = new();
    private readonly Mock<IAuditEventRepository> _auditEvents = new();
    private readonly Mock<IConversationSessionRepository> _sessions = new();
    private readonly Mock<IVerificationGrantRepository> _grants = new();
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IGrantAuthorizationService> _grantAuth = new();

    public SubmitConfirmedIntentMutationAuthorizationTests()
    {
        _hasher.Setup(h => h.Hash(It.IsAny<JsonNode>())).Returns(new CanonicalHash("canon.v1", "H"));
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        _idem.Setup(r => r.GetByKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        _idem.Setup(r => r.ElectCreatorAsync(It.IsAny<IdempotencyRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _intent.Setup(r => r.IntentExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _sessions.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationSession?)null);

        _bookings.Setup(r => r.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                CustomerId = CustomerId,
                LifecycleStatus = BookingLifecycleStatus.Confirmed,
            });

        _grantAuth.Setup(g => g.RequireGrantScopeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerificationGrant { TenantId = TenantId, GrantId = GrantId });
    }

    private SubmitConfirmedIntentCommandHandler CreateHandler() =>
        new(_hasher.Object, _uow.Object, _idem.Object, _intent.Object, _ops.Object, _outbox.Object,
            _auditEvents.Object, new Cordia.Infrastructure.Observability.Metrics(), _sessions.Object,
            _grants.Object, _bookings.Object, _grantAuth.Object);

    /// <summary>
    /// The grant is SPENT when the request creates a new operation.
    ///
    /// <para>Every dedicated door consumes; this one validated and moved on, so a modify/reschedule
    /// grant stayed replayable for its whole validity window — the single-use property of §9.3 was
    /// absent from the only door a reschedule can use.</para>
    /// </summary>
    [Fact]
    public async Task AMutationThatCreatesAnOperationSpendsItsGrant()
    {
        await CreateHandler().Handle(MakeCommand(), TestContext.Current.CancellationToken);

        _grantAuth.Verify(g => g.ConsumeGrantAsync(
            TenantId, GrantId, CustomerId, VerificationActionEnum.Modify, BookingId, "booking",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A replay must NOT be refused by the fence its own first attempt set.
    ///
    /// <para>This is why consumption is gated on winning the election rather than done in the
    /// pre-check: the same key with the same hash is the same request, and answering it with a 403
    /// because the original succeeded would make an honest network retry look like an authorization
    /// failure.</para>
    /// </summary>
    [Fact]
    public async Task AReplayOfTheSameRequestNeitherSpendsTheGrantAgainNorIsRefused()
    {
        _idem.Setup(r => r.GetByKeyAsync(TenantId, "idem_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord
            {
                TenantId = TenantId,
                IdempotencyKey = "idem_1",
                OperationId = "op_1",
                RequestHash = "canon.v1:H",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var result = await CreateHandler().Handle(MakeCommand(), TestContext.Current.CancellationToken);

        Assert.IsType<Accepted>(result);
        _grantAuth.Verify(g => g.ConsumeGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A denial raised BEFORE the grant service still leaves a trail (spec 14 §3.1 step 5).
    ///
    /// <para><see cref="IGrantAuthorizationService"/> audits its own denials, but an unknown target
    /// booking is refused earlier, and the command is not an <c>IOperatorActionRequest</c> — so a
    /// caller probing this door with booking ids was turned away leaving no record of having tried.
    /// It routes through the grant service rather than the handler's own writer so the row keeps
    /// that service's fresh-scope guarantee and survives the rollback the 403 causes.</para>
    /// </summary>
    [Fact]
    public async Task AnUnknownTargetBookingIsDeniedAndAudited()
    {
        _bookings.Setup(r => r.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Booking?)null);

        await Assert.ThrowsAsync<VerificationRequiredException>(
            () => CreateHandler().Handle(MakeCommand(), TestContext.Current.CancellationToken));

        _grantAuth.Verify(g => g.WriteDenyAsync(
            TenantId, It.IsAny<string?>(), It.IsAny<string>(), VerificationActionEnum.Modify,
            BookingId, "booking", "unknown_target_booking",
            It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>The staff-role path authorizes on the asserted role, so it has no grant to spend.</summary>
    [Fact]
    public async Task TheStaffRolePathSpendsNothing()
    {
        var command = MakeCommand() with { GrantId = null, ActorId = "ops", ActorRole = "operator" };

        await CreateHandler().Handle(command, TestContext.Current.CancellationToken);

        _grantAuth.Verify(g => g.ConsumeGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SubmitConfirmedIntentCommand MakeCommand()
    {
        var payload = new BookingRequest
        {
            Operation = OperationTypeEnum.Modify,
            TargetBookingId = BookingId,
            ServiceId = "svc_1",
            LocationId = "loc_1",
        };

        var request = new SubmitConfirmedIntentRequest
        {
            IdempotencyKey = "idem_1",
            OperationId = "op_1",
            IntentId = "int_1",
            IntentRevision = 1,
            Revision = new IntentRevision
            {
                IntentId = "int_1",
                Revision = 1,
                CanonicalPayload = payload,
                SchemaVersion = "v1",
                TenantPolicyVersion = "v1",
                SourceSessionId = "sess_1",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            Confirmation = new Confirmation
            {
                ConfirmationId = "conf_1",
                IntentId = "int_1",
                IntentRevision = 1,
                IntentPayloadHash = "H",
                ConfirmationChannel = ChannelEnum.Email,
                Identity = CustomerId,
                ConfirmedAt = DateTimeOffset.UtcNow,
            },
        };

        return new SubmitConfirmedIntentCommand(TenantId, request, GrantId: GrantId);
    }
}
