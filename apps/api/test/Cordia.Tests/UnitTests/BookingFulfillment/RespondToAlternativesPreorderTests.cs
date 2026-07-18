using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Spec 11 §5.3 / Inv-8 — a pre-order operation parked in <c>AlternativesRequired</c> (no-capacity or
/// stale revalidation) already has its single combined outcome owned by the saga. A late "waitlist me"
/// response must NOT move it to <c>Waitlisted</c> and emit a second, contradictory customer
/// notification (review PR #65, finding 4). Decline stays allowed (routes to the saga signal).
/// </summary>
[Trait("Category", "Unit")]
public class RespondToAlternativesPreorderTests
{
    private const string TenantId = "t_1";
    private const string OperationId = "op_pre_1";

    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public RespondToAlternativesPreorderTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private RespondToAlternativesCommandHandler CreateHandler() =>
        new(_opRepo.Object, _outboxRepo.Object, _auditRepo.Object, _uow.Object);

    private static BookingOperation PreorderOp() => new()
    {
        TenantId = TenantId,
        OperationId = OperationId,
        IntentId = "int_1",
        IntentRevision = 1,
        Status = OperationStatus.AlternativesRequired,
        IsPreorder = true,
        Version = 3,
    };

    [Fact]
    public async Task Waitlist_OnPreorderOperation_IsRejected_NoTransition_NoNotification()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreorderOp());

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            CreateHandler().Handle(
                new RespondToAlternativesCommand(TenantId, OperationId, AlternativesResponse.Waitlist),
                CancellationToken.None));

        // No state transition and no waitlisted notification for a pre-order op.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _outboxRepo.Verify(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Waitlist_OnRegularOperation_StillTransitionsToWaitlisted()
    {
        var op = PreorderOp();
        op.IsPreorder = false;
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                TenantId, OperationId, op.Version,
                OperationStatus.AlternativesRequired, OperationStatus.Waitlisted,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(
            new RespondToAlternativesCommand(TenantId, OperationId, AlternativesResponse.Waitlist),
            CancellationToken.None);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            TenantId, OperationId, op.Version,
            OperationStatus.AlternativesRequired, OperationStatus.Waitlisted,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _outboxRepo.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.waitlisted.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
