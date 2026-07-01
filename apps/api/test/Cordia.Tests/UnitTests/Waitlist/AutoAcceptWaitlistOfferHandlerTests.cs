using System.Text.Json.Nodes;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.Waitlist.AutoAccept;
using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AcceptanceResult = Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent.AcceptanceResult;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Spec 11 §5.1 / §20 (auto-book, Inv-1) — a match that satisfies a recorded <c>AutoBookGrant</c>
/// is auto-booked through the SAME spec-04 front door a customer accept uses, minting an auditable
/// confirmed revision that records the grant's consent evidence, instead of finalizing capacity +
/// emitting a bespoke event. These tests pin: (1) a permitted grant submits a hash-matching
/// <c>SubmitConfirmedIntentCommand</c> stamped with identity + consent evidence, then claims
/// <c>Offered → Accepted</c> and audits <c>waitlist.auto_booked</c>; (2) a grant that no longer
/// permits settles WITHOUT booking (§20 fail-secure); (3) an already-Accepted offer is an idempotent
/// re-submit that does not re-claim; (4) a non-live offer settles without booking.
/// </summary>
[Trait("Category", "Unit")]
public class AutoAcceptWaitlistOfferHandlerTests
{
    private const string TenantId = "t_1";
    private const string OfferId = "wlo_1";
    private const string EnrollmentId = "wle_1";
    private const string OperationId = "op_wl_1";
    private const string ConsentEvidenceId = "consent_1";

    private readonly Mock<IWaitlistOfferRepository> _offerRepo = new();
    private readonly Mock<IWaitlistEnrollmentRepository> _enrollmentRepo = new();
    private readonly Mock<ICanonicalRequestHasher> _hasher = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private readonly List<SubmitConfirmedIntentCommand> _submitted = new();

    public AutoAcceptWaitlistOfferHandlerTests()
    {
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _hasher.Setup(h => h.Hash(It.IsAny<JsonNode>())).Returns(new CanonicalHash("canon.v1", "H"));
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
        _mediator.Setup(m => m.Send(It.IsAny<SubmitConfirmedIntentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<AcceptanceResult>, CancellationToken>((c, _) => _submitted.Add((SubmitConfirmedIntentCommand)c))
            .ReturnsAsync(new Accepted(new OperationAcceptedResponse { OperationId = OperationId, Status = "accepted" }));
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private AutoAcceptWaitlistOfferCommandHandler CreateHandler() => new(
        _offerRepo.Object, _enrollmentRepo.Object, _hasher.Object,
        _mediator.Object, _auditRepo.Object,
        new WaitlistAcceptReconcileScheduler(_jobs.Object), _uow.Object,
        NullLogger<AutoAcceptWaitlistOfferCommandHandler>.Instance);

    [Fact]
    public async Task AutoAccept_Permitted_Submits_With_ConsentEvidence_And_Claims()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Offered));
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEnrollment(PermittingGrant()));
        _offerRepo.Setup(r => r.TryResolveAsync(
                TenantId, OfferId, It.IsAny<long>(),
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(new AutoAcceptWaitlistOfferCommand(TenantId, OfferId), CancellationToken.None);

        var cmd = Assert.Single(_submitted);
        var req = cmd.Request;
        Assert.Equal(OperationId, req.OperationId);
        Assert.Equal("H", req.Confirmation.IntentPayloadHash);
        Assert.Equal("id_cust_1", req.Confirmation.Identity);
        // §20: the recorded consent evidence is stamped on the confirmation for audit.
        Assert.Contains($"auto_book_consent:{ConsentEvidenceId}", req.Confirmation.DisclosuresPresented!);

        _offerRepo.Verify(r => r.TryResolveAsync(
            TenantId, OfferId, It.IsAny<long>(),
            WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "waitlist.auto_booked"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoAccept_GrantNoLongerPermits_Settles_Without_Booking()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Offered));
        // MaxPartySize below the offer party size (2) → Permits returns false.
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEnrollment(PermittingGrant() with { MaxPartySize = 1 }));

        await CreateHandler().Handle(new AutoAcceptWaitlistOfferCommand(TenantId, OfferId), CancellationToken.None);

        Assert.Empty(_submitted);
        _offerRepo.Verify(r => r.TryResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<WaitlistOfferStatus>(), It.IsAny<WaitlistOfferStatus>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoAccept_AlreadyAccepted_Resubmits_And_Does_Not_Reclaim()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Accepted));
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEnrollment(PermittingGrant()));

        await CreateHandler().Handle(new AutoAcceptWaitlistOfferCommand(TenantId, OfferId), CancellationToken.None);

        Assert.Single(_submitted); // idempotent replay through spec-04
        _offerRepo.Verify(r => r.TryResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<WaitlistOfferStatus>(), It.IsAny<WaitlistOfferStatus>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoAccept_NonLiveOffer_Settles_Without_Booking()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Expired));

        await CreateHandler().Handle(new AutoAcceptWaitlistOfferCommand(TenantId, OfferId), CancellationToken.None);

        Assert.Empty(_submitted);
    }

    private static AutoBookGrant PermittingGrant() => new(
        ConsentEvidenceId,
        DateTimeOffset.UtcNow.AddDays(-2),
        MaxPartySize: 4,
        ConstraintFrom: DateTimeOffset.UtcNow.AddDays(-1),
        ConstraintTo: DateTimeOffset.UtcNow.AddDays(30),
        MaxDepositAmountMinor: 100_000,
        DepositCurrency: "AUD");

    private static WaitlistOffer SampleOffer(WaitlistOfferStatus status) => new()
    {
        TenantId = TenantId,
        OfferId = OfferId,
        EnrollmentId = EnrollmentId,
        OperationId = OperationId,
        HoldId = "hold_1",
        SlotId = "slot_1",
        SlotStartAt = DateTimeOffset.UtcNow.AddDays(1),
        AvailableSeats = 2,
        Status = status,
        Version = 1,
    };

    private static WaitlistEnrollment SampleEnrollment(AutoBookGrant grant) => new()
    {
        TenantId = TenantId,
        EnrollmentId = EnrollmentId,
        CustomerRef = "cust_1",
        ServiceId = "svc_1",
        LocationId = "loc_1",
        PartySize = 2,
        NotificationChannel = "email",
        IdentityRef = "id_cust_1",
        VerificationLevel = "channel_verified",
        AutoBook = grant.ToJson(),
    };
}
