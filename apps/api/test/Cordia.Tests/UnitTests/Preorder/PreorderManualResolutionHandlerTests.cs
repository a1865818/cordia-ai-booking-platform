using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.ManualResolution;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §21.4 — an operator + customer resolve a pre-order parked in <c>ManualReview</c>: convert
/// it to a standalone order (→ Completed, marked standalone, audited with both parties) or cancel it
/// (→ FailedFinal). The customer half of the convert is a real single-use spec-08 verification grant
/// (<c>Modify</c>, scoped to the committed booking) consumed via <see cref="IGrantAuthorizationService"/>.
/// These tests pin the guarded advance, grant consumption + its 403 on an invalid grant, the audit
/// evidence, idempotency on an already-resolved saga, and the 404/409 rejections.
/// </summary>
[Trait("Category", "Unit")]
public class PreorderManualResolutionHandlerTests
{
    private const string TenantId = "t_1";
    private const string SagaId = "pos_1";
    private const string OperationId = "op_1";
    private const string Operator = "op_alice";
    private const string GrantId = "grant_1";
    private const string CustomerRef = "cust_1";
    private const string BookingId = "bkg_1";

    private readonly Mock<IPreorderSagaRepository> _sagaRepo = new();
    private readonly Mock<IBookingReadRepository> _bookingRead = new();
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IGrantAuthorizationService> _grants = new();
    private readonly Mock<IPreorderItemReservation> _items = new();
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

    /// <summary>Resolves the committed booking (operation → projection → booking) so the convert can
    /// scope + consume the customer grant. Call in convert tests that reach the grant step.</summary>
    private void SetupCommittedBooking()
    {
        _bookingRead.Setup(r => r.GetBookingProjectionByOperationIdAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingProjection(
                BookingId, OperationId, null, null, null, null, null, null,
                "booked", OperationStatus.Booked, OperationTypeEnum.Create, 0, DateTimeOffset.UtcNow));
        _bookings.Setup(r => r.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking { TenantId = TenantId, BookingId = BookingId, CustomerId = CustomerRef });
    }

    private ConvertPreorderToStandaloneCommandHandler Convert() => new(
        _sagaRepo.Object, _bookingRead.Object, _bookings.Object, _grants.Object,
        _outbox.Object, new AuditWriter(_audit.Object), _uow.Object,
        NullLogger<ConvertPreorderToStandaloneCommandHandler>.Instance);

    private CancelPreorderManualReviewCommandHandler Cancel() => new(
        _sagaRepo.Object, _items.Object, _outbox.Object, new AuditWriter(_audit.Object), _uow.Object,
        NullLogger<CancelPreorderManualReviewCommandHandler>.Instance);

    private void SetupAdvance(PreorderSagaState from, PreorderSagaState to, int rows) =>
        _sagaRepo.Setup(r => r.TryAdvanceStateAsync(
                TenantId, SagaId, It.IsAny<long>(), from, to, It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

    private static PreorderSaga Saga(PreorderSagaState state, bool isStandalone = false) => new()
    {
        TenantId = TenantId,
        SagaId = SagaId,
        OperationId = OperationId,
        SagaState = state,
        IsStandalone = isStandalone,
        Version = 3,
    };

    // ── Convert ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_FromManualReview_Advances_SetsStandalone_ConsumesGrant_Emits_And_Audits()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        SetupCommittedBooking();
        SetupAdvance(PreorderSagaState.ManualReview, PreorderSagaState.Completed, 1);

        await Convert().Handle(
            new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None);

        // Advances ManualReview → Completed, marking the saga standalone (§21.4).
        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            TenantId, SagaId, It.IsAny<long>(),
            PreorderSagaState.ManualReview, PreorderSagaState.Completed, It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), true,
            It.IsAny<CancellationToken>()), Times.Once);
        // Customer half (§21.4): consumes the single-use grant scoped to the committed booking (Modify).
        _grants.Verify(g => g.ConsumeGrantAsync(
            TenantId, GrantId, CustomerRef, VerificationActionEnum.Modify, BookingId,
            It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.BookingConfirmedNotification),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "preorder.standalone_converted" && e.Actor == Operator),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Convert_InvalidGrant_Throws_403_And_ConsumesAttempted()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        SetupCommittedBooking();
        SetupAdvance(PreorderSagaState.ManualReview, PreorderSagaState.Completed, 1);
        // A missing/expired/mismatched grant fails the consume → VerificationRequiredException (403).
        _grants.Setup(g => g.ConsumeGrantAsync(
                TenantId, GrantId, CustomerRef, VerificationActionEnum.Modify, BookingId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VerificationRequiredException(TenantId, CustomerRef, VerificationActionEnum.Modify, BookingId));

        await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None));

        _grants.Verify(g => g.ConsumeGrantAsync(
            TenantId, GrantId, CustomerRef, VerificationActionEnum.Modify, BookingId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Convert_NoCommittedBooking_Throws_403_WithoutAdvancing()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        _bookingRead.Setup(r => r.GetBookingProjectionByOperationIdAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingProjection?)null);

        await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None));

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _grants.Verify(g => g.ConsumeGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Convert_AlreadyConvertedToStandalone_IsIdempotent_NoAdvance_NoGrantConsumed()
    {
        // A prior convert leaves the saga Completed AND IsStandalone — only that is an idempotent replay.
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.Completed, isStandalone: true));

        await Convert().Handle(
            new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        // An idempotent replay must NOT burn a fresh single-use grant.
        _grants.Verify(g => g.ConsumeGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Convert_CompletedButNotStandalone_Throws_409()
    {
        // A normally-completed pre-order (Completed but NOT standalone) is not a convert target — it must
        // be rejected as an invalid transition rather than silently treated as an idempotent success.
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.Completed, isStandalone: false));

        await Assert.ThrowsAsync<InvalidPreorderSagaTransitionException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None));

        _grants.Verify(g => g.ConsumeGrantAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Convert_NotManualReview_Throws_409()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.CommittingBooking));

        await Assert.ThrowsAsync<InvalidPreorderSagaTransitionException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None));
    }

    [Fact]
    public async Task Convert_Unknown_Throws_404()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreorderSaga?)null);

        await Assert.ThrowsAsync<PreorderSagaNotFoundException>(() =>
            Convert().Handle(new ConvertPreorderToStandaloneCommand(TenantId, SagaId, Operator, GrantId, "rc_test", "operator"), CancellationToken.None));
    }

    // ── Cancel ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_FromManualReview_Advances_FailedFinal_Emits_And_Audits()
    {
        _sagaRepo.Setup(r => r.GetAsync(SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Saga(PreorderSagaState.ManualReview));
        SetupAdvance(PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal, 1);

        await Cancel().Handle(
            new CancelPreorderManualReviewCommand(TenantId, SagaId, Operator, Reason: "customer_declined", ReasonCode: "rc_test", ActorRole: "operator"), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            TenantId, SagaId, It.IsAny<long>(),
            PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal, It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // §5.3 "FailedFinal: holds/items released" — the parked saga's item reservation is released.
        _items.Verify(i => i.ReleaseAsync(TenantId, SagaId, It.IsAny<CancellationToken>()), Times.Once);
        // C14: the booking is committed and stays confirmed — the cancel outcome must be the
        // dedicated "pre-order cancelled, booking stands" event, never the FinalFailure template.
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaSupport.PreorderCancelledEventType),
            It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaSupport.OutcomeFailedEventType),
            It.IsAny<CancellationToken>()), Times.Never);
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
            new CancelPreorderManualReviewCommand(TenantId, SagaId, Operator, Reason: null, ReasonCode: "rc_test", ActorRole: "operator"), CancellationToken.None);

        _sagaRepo.Verify(r => r.TryAdvanceStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<PreorderSagaState>(), It.IsAny<PreorderSagaState>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
