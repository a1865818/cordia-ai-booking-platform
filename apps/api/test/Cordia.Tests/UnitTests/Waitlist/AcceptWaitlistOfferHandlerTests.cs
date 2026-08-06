using System.Text.Json.Nodes;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.Waitlist.Accept;
using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Moq;
// Disambiguate from Cordia.Domain.Generated.AcceptanceResult (a different, unrelated generated type).
using AcceptanceResult = Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent.AcceptanceResult;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Spec 11 §5.1 (divergence #1) — accepting a waitlist offer routes through the SAME spec 04 confirmed
/// -booking front door a direct booking uses, instead of finalizing its own hold + emitting a bespoke
/// event. These tests pin: (1) the accept submits a hash-matching <c>SubmitConfirmedIntentCommand</c>
/// under the offer's operation id, stamped with the enrolled customer's captured identity (Inv-1) and
/// then claims the offer <c>Offered → Accepted</c>; (2) an already-Accepted offer is an idempotent
/// re-submit (safe 04 replay) that does not re-claim; (3) a terminal offer is a 409.
/// </summary>
[Trait("Category", "Unit")]
public class AcceptWaitlistOfferHandlerTests
{
    private const string TenantId = "t_1";
    private const string OfferId = "wlo_1";
    private const string EnrollmentId = "wle_1";
    private const string OperationId = "op_wl_1";

    private readonly Mock<IWaitlistOfferRepository> _offerRepo = new();
    private readonly Mock<IWaitlistEnrollmentRepository> _enrollmentRepo = new();
    private readonly Mock<ICanonicalRequestHasher> _hasher = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private readonly List<SubmitConfirmedIntentCommand> _submitted = new();

    public AcceptWaitlistOfferHandlerTests()
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
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEnrollment());
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private AcceptWaitlistOfferCommandHandler CreateHandler() => new(
        _offerRepo.Object, _enrollmentRepo.Object, _hasher.Object,
        _mediator.Object, _auditRepo.Object,
        new WaitlistAcceptReconcileScheduler(_jobs.Object),
        new AuditWriter(_auditRepo.Object), _uow.Object);

    [Fact]
    public async Task Accept_Submits_HashMatching_Confirmed_Intent_Under_Offer_Operation_And_Claims()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Offered));
        _offerRepo.Setup(r => r.TryResolveAsync(
                TenantId, OfferId, It.IsAny<long>(),
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None);

        var cmd = Assert.Single(_submitted);
        var req = cmd.Request;
        Assert.Equal(OperationId, req.OperationId); // minted under the hold's operation id
        Assert.Equal("H", req.Revision.PayloadHash);
        // §8.8 — confirmation hash must equal the computed canonical hash so spec 04 accepts it.
        Assert.Equal("H", req.Confirmation.IntentPayloadHash);
        // Divergence #1 — bound to the enrolled customer's captured identity, not the notification viewer.
        Assert.Equal("id_cust_1", req.Confirmation.Identity);
        Assert.Equal("channel_verified", req.Confirmation.VerificationLevel);
        Assert.Equal(OperationTypeEnum.Create, req.Revision.CanonicalPayload.Operation);

        // The offer is claimed Offered → Accepted.
        _offerRepo.Verify(r => r.TryResolveAsync(
            TenantId, OfferId, It.IsAny<long>(),
            WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);

        // Claim won → schedule the accept-reconcile probe that marks the enrollment fulfilled once 06
        // books, so the enrollee is never re-offered (or auto-booked) a second table (finding 3).
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Accept_AlreadyAccepted_Resubmits_And_Does_Not_Reclaim()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Accepted));

        await CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None);

        // Safe replay: still submits to 04 (idempotent key) so a crash after the claim self-heals ...
        Assert.Single(_submitted);
        // ... but never re-claims the already-terminal offer.
        _offerRepo.Verify(r => r.TryResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<WaitlistOfferStatus>(), It.IsAny<WaitlistOfferStatus>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accept_AlreadyAccepted_EnrollmentFulfilledOrCancelled_IsStillIdempotentReplay()
    {
        // The reconcile path marks FULFILLMENT by stamping CancelledAt (CancelAsync). A legitimate
        // accept retry (duplicate delivery / retry after a crash) must stay the documented no-op —
        // not turn into a 409 for a booking that already exists (review PR #65, finding 7).
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Accepted));
        var fulfilled = SampleEnrollment();
        fulfilled.CancelledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fulfilled);

        await CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None);

        // Safe replay: re-submits to 04 (idempotent key) and never re-claims.
        Assert.Single(_submitted);
        _offerRepo.Verify(r => r.TryResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<WaitlistOfferStatus>(), It.IsAny<WaitlistOfferStatus>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accept_Fresh_CancelledEnrollment_Throws_Invalid_Transition()
    {
        // A FRESH accept (offer still Offered) on a cancelled enrollment must not mint a booking.
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Offered));
        var cancelled = SampleEnrollment();
        cancelled.CancelledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);

        await Assert.ThrowsAsync<InvalidWaitlistOfferTransitionException>(() =>
            CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None));

        Assert.Empty(_submitted);
    }

    [Fact]
    public async Task Accept_Terminal_Offer_Throws_Invalid_Transition()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Declined));

        await Assert.ThrowsAsync<InvalidWaitlistOfferTransitionException>(() =>
            CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None));

        Assert.Empty(_submitted);
    }

    [Fact]
    public async Task Accept_LostClaimAfterMint_SchedulesReconcile_DoesNotThrow()
    {
        // Offer is Offered at read time (so we mint), but a concurrent expire/decline wins the claim:
        // TryResolve returns 0 and the reload shows a terminal non-Accepted state.
        _offerRepo.SetupSequence(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Offered))
            .ReturnsAsync(SampleOffer(WaitlistOfferStatus.Expired));
        _offerRepo.Setup(r => r.TryResolveAsync(
                TenantId, OfferId, It.IsAny<long>(),
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // The booking was already minted, so the accept must NOT throw 409...
        await CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None);

        Assert.Single(_submitted); // booking minted (record-before-state)
        // ...and it schedules a reconcile to settle the orphaned operation against the enrollment.
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Accept_Unknown_Offer_Throws_NotFound()
    {
        _offerRepo.Setup(r => r.GetAsync(OfferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WaitlistOffer?)null);

        await Assert.ThrowsAsync<WaitlistOfferNotFoundException>(() =>
            CreateHandler().Handle(new AcceptWaitlistOfferCommand(TenantId, OfferId, "cust_1", "operator", "waitlist test"), CancellationToken.None));
    }

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

    private static WaitlistEnrollment SampleEnrollment() => new()
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
    };
}
