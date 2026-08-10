using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="RespondToAlternativesCommandHandler"/> (spec 06 §6.1, §11, T11a).
/// </summary>
[Trait("Category", "Unit")]
public class RespondToAlternativesCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    private readonly Mock<IBookingRepository> _bookingRepo = new();

    private RespondToAlternativesCommandHandler CreateHandler() => new(
        _opRepo.Object,
        _outboxRepo.Object,
        _auditRepo.Object,
        _bookingRepo.Object,
        _uow.Object);

    private static BookingOperation MakeOp(string tenantId, string operationId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = version,
        };

    public RespondToAlternativesCommandHandlerTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        _outboxRepo.Setup(r => r.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    // ── Waitlist path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waitlist_TransitionsToWaitlisted_EmitsNotificationAndAudit()
    {
        var tenantId = "t-test";
        var operationId = "op-wl";
        var op = MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 2);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        var handler = CreateHandler();
        await handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.AlternativesRequired, OperationStatus.Waitlisted,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.waitlisted.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.waitlisted"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Decline path ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_TransitionsToFailedFinal_EmitsNotificationAndAudit()
    {
        var tenantId = "t-test";
        var operationId = "op-decline";
        var op = MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 3);

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        var handler = CreateHandler();
        await handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline), CancellationToken.None);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, operationId, op.Version,
            OperationStatus.AlternativesRequired, OperationStatus.FailedFinal,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.booking_failed.v1"),
            It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "operation.failed_final"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A declined MUTATION hands the booking row its lifecycle back.
    ///
    /// <para><c>POST /api/Bookings/{id}/modifications</c> parks the booking at
    /// <c>ModificationPending</c> and is its only writer; only the automatic SUCCESS paths ever
    /// cleared it. Declining the alternatives prompt terminalizes the operation at
    /// <c>FailedFinal</c> — nothing looks at it again — so the booking went on telling every reader
    /// a change was outstanding, permanently, while the operation said the change had failed.</para>
    /// </summary>
    [Fact]
    public async Task Decline_RevertsAPendingModification_SoTheBookingStopsClaimingAChangeIsInFlight()
    {
        var tenantId = "t-test";
        var operationId = "op-decline-modify";
        var op = MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 3);
        op.OperationType = OperationTypeEnum.Modify;
        op.BookingId = "bk-1";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        _bookingRepo.Setup(b => b.GetByIdAsync(tenantId, "bk-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = tenantId,
                BookingId = "bk-1",
                BookingReference = "CORD-1",
                Version = 7,
                LifecycleStatus = BookingLifecycleStatus.ModificationPending,
            });

        var handler = CreateHandler();
        await handler.Handle(
            new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline),
            CancellationToken.None);

        // Released through the CAS, so the pending state is retracted only if this operation still
        // owns it — a second modification parking the booking in the meantime affects zero rows
        // rather than having its parking cleared out from under it.
        _bookingRepo.Verify(b => b.TryReleasePendingAsync(
                tenantId, "bk-1", 7,
                BookingLifecycleStatus.ModificationPending, operationId,
                BookingLifecycleStatus.Confirmed,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A create has no existing booking to un-park, so nothing is touched.</summary>
    [Fact]
    public async Task Decline_OfACreate_TouchesNoBookingLifecycle()
    {
        var tenantId = "t-test";
        var operationId = "op-decline-create";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.AlternativesRequired, version: 3));

        var handler = CreateHandler();
        await handler.Handle(
            new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline),
            CancellationToken.None);

        _bookingRepo.Verify(b => b.TryReleasePendingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<string>(),
                It.IsAny<BookingLifecycleStatus>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Guard: wrong status ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OpNotInAlternativesRequired_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-wrong-status";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, operationId, OperationStatus.Accepted));

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None));
    }

    // ── Guard: not found ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OpNotFound_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-notfound";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline), CancellationToken.None));
    }

    // ── Guard: tenant mismatch ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_TenantMismatch_ThrowsOperationConflictException()
    {
        var tenantId = "t-test";
        var operationId = "op-mismatch";

        _opRepo.Setup(r => r.GetAsync(operationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp("different-tenant", operationId, OperationStatus.AlternativesRequired));

        var handler = CreateHandler();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            handler.Handle(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist), CancellationToken.None));
    }
}
