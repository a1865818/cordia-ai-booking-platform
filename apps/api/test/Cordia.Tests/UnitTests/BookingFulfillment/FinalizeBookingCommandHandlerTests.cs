using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit-test harness for <see cref="FinalizeBookingCommandHandler"/>.
///
/// <para><b>Why this exists.</b> The handler has seventeen constructor dependencies and, until this
/// file, <b>no unit test called <c>Handle()</c> at all</b> — the two unit tests that mentioned it
/// (<see cref="FinalizeBookingProvenanceTests"/> and the hardening suite) exercise
/// <c>internal static</c> helpers only, and the sole test that drives the handler proper is an
/// integration test going through <c>IMediator</c>. Four open issues against this handler cite the
/// missing harness, so it is built once, here, before the riskier fixes need it.</para>
///
/// <para><b>Deliberately narrow.</b> This covers the three <i>guard</i> branches that run before any
/// transaction opens — the ones every later change has to keep intact. It does NOT cover the TXN1 /
/// TXN2 provider paths. Those are exactly the behaviour issues #51, #55, #56, #57 and #58 intend to
/// change, and pinning them now would encode the current (wrong) routing as expected, so a correct
/// fix would arrive looking like a regression.</para>
///
/// <para><b>The trap this harness makes explicit.</b> <see cref="FinalizeOutcome"/> declares
/// <c>Finalized, HoldNoLongerValid, NotTracked</c> in that order, so its <b>zero value is
/// <c>Finalized</c></b> — an unconfigured <c>Mock&lt;ICapacityReservation&gt;</c> silently answers
/// "hold finalized, proceed to Booked". That is the benign direction, which is worse than the loud
/// one: a test that forgets to configure capacity tests the happy path while appearing to test the
/// branch. <see cref="CreateHandler"/> therefore sets it explicitly rather than relying on the
/// default, the same way this suite's neighbour spells out <c>OperationTypeEnum.Create</c> because
/// <c>Cancel</c> is that enum's zero value.</para>
/// </summary>
[Trait("Category", "Unit")]
public class FinalizeBookingCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<IWorkflowAttemptRepository> _attemptRepo = new();
    private readonly Mock<IBookingIntentRepository> _intentRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IBookingProviderAdapter> _adapter = new();
    private readonly Mock<IBookingRepository> _bookingRepo = new();
    private readonly Mock<IProviderCircuitBreaker> _circuitBreaker = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ICapacityReservation> _capacity = new();
    private readonly Mock<IGuestContactRepository> _guestContacts = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();

    private FinalizeBookingCommandHandler CreateHandler() =>
        new(_opRepo.Object, _stepRepo.Object, _attemptRepo.Object, _intentRepo.Object,
            _outboxRepo.Object, _auditRepo.Object, _adapter.Object, _bookingRepo.Object,
            _circuitBreaker.Object, new RetryClassifier(), _retryPolicy.Object,
            new RetryScheduler(_jobs.Object, _stepRepo.Object), _uow.Object, _capacity.Object,
            _guestContacts.Object, NullLogger<FinalizeBookingCommandHandler>.Instance,
            new Metrics());

    public FinalizeBookingCommandHandlerTests()
    {
        _adapter.Setup(a => a.ProviderName).Returns("stub");

        // UoW executes work immediately.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Set explicitly even though Finalized is the zero value — see the class remarks. A test
        // that means "the hold is still good" must say so, not inherit it from enum ordering.
        _capacity
            .Setup(c => c.FinalizeAgainstOperationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinalizeOutcome.Finalized);

        _retryPolicy
            .Setup(r => r.NextEligibleAt(
                It.IsAny<StepRetryBudget>(), It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .Returns(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    private static BookingOperation MakeOp(
        string tenantId,
        string operationId,
        OperationStatus status,
        long version = 0,
        OperationTypeEnum operationType = OperationTypeEnum.Create) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
            // Explicit: Cancel is the ZERO value of the generated enum, so leaving it unset would
            // silently make every operation here a cancellation.
            OperationType = operationType,
        };

    private static WorkflowAttempt MakeAttempt(
        string tenantId, string operationId, AttemptOutcome outcome, string? providerRef = null) =>
        new()
        {
            TenantId = tenantId,
            AttemptId = "att-1",
            OperationId = operationId,
            WorkflowStep = "finalize",
            ProviderIdempotencyKey = "pik_test",
            ProviderName = "stub",
            ProviderAccount = "default",
            RequestHash = "h",
            AttemptNumber = 1,
            Outcome = outcome,
            ProviderRef = providerRef,
            RequestMeta = "{}",
            ResponseMeta = "{}",
        };

    // ── Guard 1: unknown operation, or one belonging to another tenant → Wait ────────────────

    [Fact]
    public async Task Handle_OperationNotFound_ReturnsWait_AndNeverCallsProvider()
    {
        _opRepo.Setup(r => r.GetAsync("op-missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var result = await CreateHandler()
            .Handle(new FinalizeBookingCommand("t1", "op-missing"), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
        _adapter.Verify(
            a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Inv-13. <c>IBookingOperationRepository.GetAsync</c> takes no tenant argument, so the tenant
    /// check is the handler's own and nothing below it re-checks. Wait rather than a terminal
    /// outcome: a foreign row is not this tenant's business to settle.
    /// </summary>
    [Fact]
    public async Task Handle_OperationBelongsToAnotherTenant_ReturnsWait_AndNeverCallsProvider()
    {
        _opRepo.Setup(r => r.GetAsync("op-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp("other-tenant", "op-1", OperationStatus.Held));

        var result = await CreateHandler()
            .Handle(new FinalizeBookingCommand("t1", "op-1"), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
        _adapter.Verify(
            a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Guard 2: only Held and Finalizing are actionable ────────────────────────────────────

    [Theory]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.CompensationRequired)]
    [InlineData(OperationStatus.ManualReview)]
    [InlineData(OperationStatus.FailedFinal)]
    [InlineData(OperationStatus.Accepted)]
    public async Task Handle_OperationNotHeldOrFinalizing_ReturnsContinue_AndNeverCallsProvider(
        OperationStatus status)
    {
        _opRepo.Setup(r => r.GetAsync("op-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp("t1", "op-1", status));

        var result = await CreateHandler()
            .Handle(new FinalizeBookingCommand("t1", "op-1"), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _adapter.Verify(
            a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Guard 3: Inv-7 — an in-flight attempt must reconcile, never re-create ───────────────

    /// <summary>
    /// Inv-7, and the most valuable branch in this file. A Dispatching or Ambiguous attempt means a
    /// request MAY already have reached the provider. Creating another one would double-book a real
    /// venue, so the operation goes to Reconciling and the driver dispatches the §16.2 lookup.
    /// </summary>
    [Theory]
    [InlineData(AttemptOutcome.Dispatching)]
    [InlineData(AttemptOutcome.Ambiguous)]
    public async Task Handle_FinalizingWithInFlightAttempt_TransitionsToReconciling_AndNeverCallsProvider(
        AttemptOutcome outcome)
    {
        var op = MakeOp("t1", "op-1", OperationStatus.Finalizing, version: 7);
        _opRepo.Setup(r => r.GetAsync("op-1", It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _attemptRepo
            .Setup(r => r.GetLatestForStepAsync("t1", "op-1", "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt("t1", "op-1", outcome));

        var result = await CreateHandler()
            .Handle(new FinalizeBookingCommand("t1", "op-1"), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);

        _opRepo.Verify(
            r => r.TryTransitionStatusAsync(
                "t1", "op-1", 7L,
                OperationStatus.Finalizing, OperationStatus.Reconciling,
                "reconcile_ambiguous", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _adapter.Verify(
            a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The documented asymmetry at the guard: it fires only for <c>Finalizing</c>. An operation back
    /// in <c>Held</c> has already been through reconcile, which proved no effect exists, so it must
    /// fall through and create — routing it to Reconciling again would loop forever.
    /// </summary>
    [Fact]
    public async Task Handle_HeldWithInFlightAttempt_DoesNotRouteToReconciling()
    {
        var op = MakeOp("t1", "op-1", OperationStatus.Held, version: 3);
        _opRepo.Setup(r => r.GetAsync("op-1", It.IsAny<CancellationToken>())).ReturnsAsync(op);
        _attemptRepo
            .Setup(r => r.GetLatestForStepAsync("t1", "op-1", "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt("t1", "op-1", AttemptOutcome.Dispatching));

        await CreateHandler().Handle(new FinalizeBookingCommand("t1", "op-1"), CancellationToken.None);

        _opRepo.Verify(
            r => r.TryTransitionStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<OperationStatus>(), OperationStatus.Reconciling,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
