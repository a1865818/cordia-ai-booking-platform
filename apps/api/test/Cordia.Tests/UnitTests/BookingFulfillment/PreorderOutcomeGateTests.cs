using Cordia.Application.Handlers.BookingFulfillment.DepositEvent;
using Cordia.Application.Handlers.BookingFulfillment.Validate;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Spec 11 §5.3 / Inv-8 — every 06 terminal emit site must branch on <c>IsPreorder</c> so a
/// pre-order operation signals the SAGA (which owns the single combined outcome and must release
/// its reserved items) instead of notifying the customer directly. Deposit-expiry and
/// validation-rejection were the two sites that missed the gate (review 2026-07-03, C12/C13):
/// the customer got a raw failure notice while the saga sat in CommittingBooking with items
/// reserved, then received a SECOND outcome when reconcile re-synthesized booking_failed.
/// </summary>
[Trait("Category", "Unit")]
public class PreorderOutcomeGateTests
{
    private const string TenantId = "t_1";
    private const string OperationId = "op_pre_1";

    private static BookingOperation Op(OperationStatus status, bool isPreorder) => new()
    {
        TenantId = TenantId,
        OperationId = OperationId,
        IntentId = "int_1",
        IntentRevision = 1,
        Status = status,
        IsPreorder = isPreorder,
        Version = 1,
    };

    // ── C12: deposit expiry ─────────────────────────────────────────────────────────────────────

    private sealed class DepositFixture
    {
        public readonly Mock<IBookingOperationRepository> OpRepo = new();
        public readonly Mock<IDepositSessionRepository> Sessions = new();
        public readonly Mock<IOutboxRepository> Outbox = new();
        public readonly Mock<IAuditEventRepository> Audit = new();

        public DepositFixture(bool isPreorder)
        {
            OpRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Op(OperationStatus.AwaitingPayment, isPreorder));
            OpRepo.Setup(r => r.TryTransitionStatusAsync(
                    TenantId, OperationId, It.IsAny<long>(),
                    OperationStatus.AwaitingPayment, OperationStatus.Expired,
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
        }

        public ProcessDepositEventCommandHandler Handler() =>
            new(OpRepo.Object, Sessions.Object, Outbox.Object, Audit.Object);
    }

    [Fact]
    public async Task DepositExpired_PreorderOp_SignalsSaga_NotCustomer()
    {
        var f = new DepositFixture(isPreorder: true);

        await f.Handler().Handle(
            new ProcessDepositEventCommand(TenantId, OperationId, DepositEventKind.Expired, "msg_1"),
            CancellationToken.None);

        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.BookingFailed),
            It.IsAny<CancellationToken>()), Times.Once);
        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.deposit_expired.v1"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DepositExpired_RegularOp_KeepsCustomerNotification()
    {
        var f = new DepositFixture(isPreorder: false);

        await f.Handler().Handle(
            new ProcessDepositEventCommand(TenantId, OperationId, DepositEventKind.Expired, "msg_1"),
            CancellationToken.None);

        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.deposit_expired.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── C13: validation-policy rejection ────────────────────────────────────────────────────────

    private sealed class ValidateFixture
    {
        public readonly Mock<IBookingOperationRepository> OpRepo = new();
        public readonly Mock<IWorkflowStepRepository> Steps = new();
        public readonly Mock<IVerificationService> Verification = new();
        public readonly Mock<IValidationPolicy> Policy = new();
        public readonly Mock<IOutboxRepository> Outbox = new();
        public readonly Mock<IAuditEventRepository> Audit = new();
        public readonly Mock<IUnitOfWork> Uow = new();

        public ValidateFixture(bool isPreorder)
        {
            // Op already Validating: skips step 1 and the workflow-step insert path.
            OpRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Op(OperationStatus.Validating, isPreorder));
            Steps.Setup(s => s.GetByOperationAndTypeAsync(TenantId, OperationId, "validate", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorkflowStep { TenantId = TenantId, OperationId = OperationId, StepType = "validate" });
            Verification.Setup(v => v.RequiresUserActionAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            Policy.Setup(p => p.CheckAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync("policy_blocked");
            OpRepo.Setup(r => r.TryTransitionStatusAsync(
                    TenantId, OperationId, It.IsAny<long>(),
                    OperationStatus.Validating, OperationStatus.Rejected,
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            Uow.Setup(u => u.ExecuteInTransactionAsync(
                    It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
        }

        public readonly Mock<IBookingRepository> Bookings = new();

        public ValidateOperationCommandHandler Handler() => new(
            OpRepo.Object, Steps.Object, Verification.Object, Policy.Object,
            Outbox.Object, Audit.Object, Bookings.Object, Uow.Object);
    }

    [Fact]
    public async Task PolicyRejected_PreorderOp_SignalsSaga_NotCustomer()
    {
        var f = new ValidateFixture(isPreorder: true);

        await f.Handler().Handle(new ValidateOperationCommand(TenantId, OperationId), CancellationToken.None);

        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == PreorderSagaMessages.BookingFailed),
            It.IsAny<CancellationToken>()), Times.Once);
        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.rejected.v1"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PolicyRejected_RegularOp_KeepsCustomerNotification()
    {
        var f = new ValidateFixture(isPreorder: false);

        await f.Handler().Handle(new ValidateOperationCommand(TenantId, OperationId), CancellationToken.None);

        f.Outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == "notification.rejected.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
