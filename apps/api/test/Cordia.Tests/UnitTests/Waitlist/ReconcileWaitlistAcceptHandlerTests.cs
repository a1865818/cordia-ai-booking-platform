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
        _opRepo.Object, _enrollmentRepo.Object, _auditRepo.Object,
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
