using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Spec 11 §5.1 — reconciling a waitlist accept whose offer claim was lost after the booking was
/// minted. The probe settles the divergence off the minted operation's real outcome:
/// Booked → mark the enrollment fulfilled (so it is never re-offered → no double-book);
/// terminal-failure → leave it eligible; still in-flight → bounded re-schedule.
/// </summary>
[Trait("Category", "Unit")]
public class ReconcileWaitlistAcceptHandlerTests
{
    private const string TenantId = "t_1";
    private const string OfferId = "wlo_1";
    private const string EnrollmentId = "wle_1";
    private const string OperationId = "op_wl_1";

    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWaitlistEnrollmentRepository> _enrollmentRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IOperationWorkItemRepository> _workItemRepo = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();

    public ReconcileWaitlistAcceptHandlerTests()
    {
        _auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _enrollmentRepo.Setup(r => r.CancelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    private ReconcileWaitlistAcceptCommandHandler CreateHandler() => new(
        _opRepo.Object, _enrollmentRepo.Object, _auditRepo.Object, _workItemRepo.Object,
        new WaitlistAcceptReconcileScheduler(_jobs.Object),
        NullLogger<ReconcileWaitlistAcceptCommandHandler>.Instance);

    private ReconcileWaitlistAcceptCommand Command(int attempt = 0) =>
        new(TenantId, "msg_1", null, OfferId, OperationId, EnrollmentId, attempt);

    [Fact]
    public async Task Booked_MarksEnrollmentFulfilled()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.Booked));
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enrollment(cancelled: false));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        // The enrollee is booked → enrollment cancelled (fulfilled) so FindEligibleAsync excludes them.
        _enrollmentRepo.Verify(r => r.CancelAsync(
            TenantId, EnrollmentId, It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Booked_AlreadyCancelledEnrollment_IsIdempotent()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.Booked));
        _enrollmentRepo.Setup(r => r.GetAsync(EnrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enrollment(cancelled: true));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OperationStatus.FailedFinal)]
    [InlineData(OperationStatus.Rejected)]
    [InlineData(OperationStatus.Expired)]
    [InlineData(OperationStatus.AlternativesRequired)]
    public async Task TerminalFailure_LeavesEnrollmentEligible(OperationStatus status)
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(status));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        // Nothing was booked → enrollee stays eligible; no cancel, no reschedule.
        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InFlight_ReschedulesNextAttempt()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.ReservingCapacity));

        await CreateHandler().Handle(Command(attempt: 0), CancellationToken.None);

        // Still in-flight → schedule one more probe; do not touch the enrollment.
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InFlight_AtMaxAttempts_SettlesWithoutReschedule()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.ReservingCapacity));

        // attempt = MaxAttempts - 1 → nextAttempt hits the cap → settle, no new job.
        await CreateHandler().Handle(Command(attempt: WaitlistAcceptReconcileScheduler.MaxAttempts - 1), CancellationToken.None);

        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// `ManualReview` is where a cancel racing this accept actually strands the operation: the
    /// released hold makes finalize return `HoldNoLongerValid`, the operation routes to
    /// `CompensationRequired`, and if the compensating provider-cancel comes back ambiguous it
    /// escalates here — with NO dispatch case in `AdvanceBookingOperationCommandHandler`, so it
    /// cannot progress without an operator.
    ///
    /// <para>It was in neither the handler's old private failure set nor
    /// `OperationStateMachine._terminalStates`, so it was treated as healthy progress and re-probed
    /// until the attempt budget ran out — then abandoned with only a log line. Probing it again can
    /// never change the answer.</para>
    /// </summary>
    [Fact]
    public async Task ManualReview_StopsProbing_AndSettlesWithAnAudit()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.ManualReview));

        await CreateHandler().Handle(Command(attempt: 0), CancellationToken.None);

        // No further probe — a human owns it now.
        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);

        // Durably recorded, so whoever resolves the review can see the waitlist consequence.
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "waitlist.accept_reconcile_settled"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Inv-16: the booking outcome is unknown, so the enrollee must NOT be stamped fulfilled —
        // that would strand them with nothing if the review resolves as failed.
        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// `CompensationRequired` is deliberately NOT treated as an ending. It has legal outgoing edges
    /// and the driver dispatches compensation on the very next iteration of the same loop, so it
    /// resolves on its own — usually in milliseconds. Settling the waitlist on it would record an
    /// outcome for a state the operation is actively leaving.
    /// </summary>
    [Fact]
    public async Task CompensationRequired_IsStillInFlight_AndKeepsProbing()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.CompensationRequired));

        await CreateHandler().Handle(Command(attempt: 0), CancellationToken.None);

        _jobs.Verify(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Abandoning the reconcile is a real decision about a guest's enrollment, so it leaves a
    /// durable record. Previously it wrote nothing at all — only a `LogWarning` — so an enrollment
    /// could be left neither fulfilled nor re-matched with no trace that the platform had stopped
    /// watching.
    /// </summary>
    [Fact]
    public async Task AtMaxAttempts_AuditsTheAbandonment()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Op(OperationStatus.ReservingCapacity));

        await CreateHandler().Handle(
            Command(attempt: WaitlistAcceptReconcileScheduler.MaxAttempts - 1), CancellationToken.None);

        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "waitlist.accept_reconcile_abandoned"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownOperation_Settles()
    {
        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _enrollmentRepo.Verify(r => r.CancelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BookingOperation Op(OperationStatus status) => new()
    {
        TenantId = TenantId,
        OperationId = OperationId,
        Status = status,
        BookingId = status == OperationStatus.Booked ? "bk_1" : null,
    };

    private static WaitlistEnrollment Enrollment(bool cancelled) => new()
    {
        TenantId = TenantId,
        EnrollmentId = EnrollmentId,
        CustomerRef = "cust_1",
        Version = 2,
        CancelledAt = cancelled ? DateTimeOffset.UtcNow : null,
    };
}
