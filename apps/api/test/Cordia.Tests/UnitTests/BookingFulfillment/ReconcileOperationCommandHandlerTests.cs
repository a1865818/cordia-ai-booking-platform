using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Reconcile;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ReconcileOperationCommandHandler"/> — verifies the §16.2 lookup
/// order (Inv-7), effect-found path (Reconciling → Booked), no-effect path (Reconciling → Held),
/// budget-exhausted path (Reconciling → ManualReview via ManualReviewEscalator),
/// the UNRESOLVED path (#55 — a lookup that failed to answer must not be read as "no effect"),
/// and that <see cref="IBookingProviderAdapter.CreateBookingAsync"/> is NEVER called.
/// </summary>
[Trait("Category", "Unit")]
public class ReconcileOperationCommandHandlerTests
{
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IWorkflowStepRepository> _stepRepo = new();
    private readonly Mock<IWorkflowAttemptRepository> _attemptRepo = new();
    private readonly Mock<IOutboxRepository> _outboxRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IBookingProviderAdapter> _adapter = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ICapacityReservation> _capacityReservation = new();

    // ManualReviewEscalator is a sealed class; wire it with a sub-set of mocked repos.
    private ManualReviewEscalator CreateEscalator() =>
        new(_opRepo.Object, new Mock<IOperationWorkItemRepository>().Object,
            _outboxRepo.Object, _auditRepo.Object);

    private ReconcileOperationCommandHandler CreateHandler() =>
        new(_opRepo.Object, _stepRepo.Object, _attemptRepo.Object,
            _outboxRepo.Object, _auditRepo.Object, _adapter.Object,
            _retryPolicy.Object, CreateEscalator(), _uow.Object,
            _capacityReservation.Object, new Metrics());

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
            // Must be explicit: `Cancel` is the ZERO value of the generated enum, so leaving it
            // unset silently makes every operation here a cancellation. These tests reconcile a
            // create — the "finalize" step path.
            OperationType = operationType,
        };

    private static WorkflowAttempt MakeAttempt(
        string tenantId, string operationId, AttemptOutcome outcome,
        string? providerRef = null, string? providerKey = null) =>
        new()
        {
            TenantId = tenantId,
            AttemptId = "att-1",
            OperationId = operationId,
            WorkflowStep = "finalize",
            ProviderIdempotencyKey = providerKey ?? "pik_test",
            ProviderName = "stub",
            ProviderAccount = "default",
            RequestHash = "h",
            AttemptNumber = 1,
            Outcome = outcome,
            ProviderRef = providerRef,
            RequestMeta = "{}",
            ResponseMeta = "{}",
        };

    public ReconcileOperationCommandHandlerTests()
    {
        _adapter.Setup(a => a.ProviderName).Returns("stub");

        // UoW executes work immediately.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));

        // Capacity settles cleanly unless a test says otherwise. Stated explicitly because
        // FinalizeOutcome.Finalized is the enum's ZERO value: an unmocked call returns "settled"
        // on its own, which is a permissive default worth making visible rather than inheriting.
        _capacityReservation.Setup(c => c.FinalizeAgainstOperationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinalizeOutcome.Finalized);

        // Default: budget not exhausted → no-effect path goes to Held.
        // Individual tests can override this to null to exercise the ManualReview path.
        _retryPolicy.Setup(r => r.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    // ── Guard: non-Reconciling op → Continue (driver re-evaluates) ───────────────────────────

    [Fact]
    public async Task Handle_OpNotReconciling_ReturnsContinue()
    {
        var (tenantId, opId) = ("t-1", "op-1");
        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Finalizing));

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Guard: null op → Wait ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_NullOp_ReturnsWait()
    {
        _opRepo.Setup(r => r.GetAsync("op-null", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingOperation?)null);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand("t-x", "op-null"), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── §16.2 lookup ORDER: key tried first ──────────────────────────────────────────────────

    [Fact]
    public async Task Handle_LookupOrder_IdempotencyKeyTriedFirst()
    {
        var (tenantId, opId) = ("t-order", "op-order");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);
        const string providerRef = "ext_ref_1";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: providerRef, providerKey: providerKey));

        // Key lookup succeeds immediately — reference + status should NOT be called.
        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, providerRef));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        // Only idempotency-key lookup was called; reference/status NOT called.
        _adapter.Verify(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.LookupByReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.GetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── §16.2 lookup ORDER: reference tried if key returns NotFound ───────────────────────────

    [Fact]
    public async Task Handle_LookupOrder_ReferenceLookup_WhenKeyNotFound()
    {
        var (tenantId, opId) = ("t-ref", "op-ref");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);
        const string providerRef = "ext_ref_2";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Dispatching, providerRef: providerRef, providerKey: providerKey));

        // Key not found; reference lookup succeeds.
        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.NotFound));
        _adapter.Setup(a => a.LookupByReferenceAsync("default", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, providerRef));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        _adapter.Verify(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.LookupByReferenceAsync("default", providerRef, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.GetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── §16.2 lookup ORDER: status tried last if reference also NotFound ──────────────────────

    [Fact]
    public async Task Handle_LookupOrder_StatusLookup_WhenKeyAndReferenceNotFound()
    {
        var (tenantId, opId) = ("t-status", "op-status");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);
        const string providerRef = "ext_ref_3";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: providerRef, providerKey: providerKey));

        // All three: key NotFound, reference NotFound, status returns AlreadyExists.
        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.NotFound));
        _adapter.Setup(a => a.LookupByReferenceAsync("default", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.NotFound));
        _adapter.Setup(a => a.GetStatusAsync("default", providerRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, providerRef));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        _adapter.Verify(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.LookupByReferenceAsync("default", providerRef, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.GetStatusAsync("default", providerRef, It.IsAny<CancellationToken>()), Times.Once);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Effect proven → op transition to Booked requested with the reference ─────────────────

    [Fact]
    public async Task Handle_EffectFound_TransitionsOpToBooked()
    {
        var (tenantId, opId) = ("t-booked", "op-booked");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);
        const string providerRef = "ext_found";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 3));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, providerRef));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Booked,
            It.IsAny<string?>(), providerRef, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Effect proven → notification outbox row added ─────────────────────────────────────────

    [Fact]
    public async Task Handle_EffectFound_AddsNotificationOutboxRow()
    {
        var (tenantId, opId) = ("t-notif", "op-notif");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);
        const string providerRef = "ext_notif";

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, providerRef));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m =>
                m.TenantId == tenantId &&
                m.OperationId == opId &&
                m.MessageType == "notification.booking_confirmed.v1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── No effect proven → transition to Held + CreateBooking NEVER called ───────────────────

    /// <summary>
    /// Modify and Reschedule need the same escape as Cancel, and for the same reasons.
    ///
    /// <para>`Created`/`AlreadyExists` prove a booking EXISTS — true of a modified booking and an
    /// unmodified one alike — so the create-shaped lookup cannot establish whether the amendment
    /// landed, and the not-proven path re-creates. A modify writes a "modify" step, never
    /// "finalize", so `step` and `latest` are always null: every redelivery reads attempt 1 of 4
    /// with 0 elapsed, the retry budget never exhausts, and the provider is re-invoked blind
    /// forever with no route to ManualReview. This became reachable when
    /// `ModifyBookingCommandHandler` started routing ambiguous outcomes here.</para>
    /// </summary>
    [Theory]
    [InlineData(OperationTypeEnum.Modify)]
    [InlineData(OperationTypeEnum.Reschedule)]
    public async Task Handle_ModifyOrReschedule_EscalatesToAHumanInsteadOfRunningTheCreateLookup(
        OperationTypeEnum operationType)
    {
        var (tenantId, opId) = ("t-modify", "op-modify");

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, operationType: operationType));
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.ManualReview,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var outcome = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, outcome);

        // No provider lookup at all, and above all no re-create.
        _adapter.Verify(a => a.LookupByIdempotencyKeyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.CreateBookingAsync(
            It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // A human, not another blind pass through Held.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.ManualReview,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Held,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CancelOperation_EscalatesToAHumanInsteadOfRunningTheCreateLookup()
    {
        // The §16.2 lookup is create-shaped: Created/AlreadyExists prove a booking EXISTS, which
        // says nothing about whether a cancellation landed, and the no-effect path RE-CREATES the
        // booking. A cancel also never writes a "finalize" step, so the budget could never
        // exhaust and this would loop forever, re-invoking the provider blind.
        var (tenantId, opId) = ("t-cancel", "op-cancel");

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling,
                operationType: OperationTypeEnum.Cancel));
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.ManualReview,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var outcome = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, outcome);

        // Not one provider lookup, and above all no re-create.
        _adapter.Verify(a => a.LookupByIdempotencyKeyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.LookupByReferenceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.GetStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.CreateBookingAsync(
            It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // It went to a human, not back to Held.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.ManualReview,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Held,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NoEffect_TransitionsToHeld_CreateNeverCalled()
    {
        var (tenantId, opId) = ("t-noeff", "op-noeff");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 2));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        // No ProviderRef on the attempt — so reference/status lookups are skipped.
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.NotFound));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Continue, result);

        // Transition to Held requested (not Booked).
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Booked transition never requested.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Booked,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // CreateBooking NEVER called by the reconcile handler (Inv-7).
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Reference/status only skipped when ProviderRef is null ───────────────────────────────

    [Fact]
    public async Task Handle_NoEffect_SkipsRefAndStatusLookup_WhenProviderRefNull()
    {
        var (tenantId, opId) = ("t-skip", "op-skip");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Dispatching, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.NotFound));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Held,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        _adapter.Verify(a => a.LookupByReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.GetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── #55: a lookup that could not answer is not an answer of "no" ────────────────────────
    //
    // `effectProven` was a positive test only, and NotFound was never tested anywhere in the
    // handler. Everything that was not proven-created therefore fell into the re-create path:
    // Reconciling → Held, which the driver walks straight back to Finalizing and a SECOND
    // CreateBookingAsync under the same idempotency key — in the same dispatch. A transport
    // error while asking "does this booking exist?" produced a duplicate booking attempt.

    [Theory]
    [InlineData(ProviderResultKind.Transient)]
    [InlineData(ProviderResultKind.Ambiguous)]
    [InlineData(ProviderResultKind.AuthError)]
    [InlineData(ProviderResultKind.PermanentError)]
    public async Task Handle_LookupNeitherProvesNorDisproves_StaysReconciling_AndDoesNotRecreate(
        ProviderResultKind kind)
    {
        var (tenantId, opId) = ("t-unresolved", $"op-unresolved-{kind}");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 4));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(kind));

        // Both destinations are mocked to SUCCEED, so a wrong route commits and is observable.
        // Without this the assertions below would hold even with the guard removed.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        // Parked, not advanced: the sweeper re-drives (Q2).
        Assert.Equal(StepOutcome.Wait, result);

        // The Inv-7 violation this phase removes: no walk-back to Held, so no second create.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Nor a Booked claim on an effect that was never proven.
        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Booked,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The in-flight attempt is left alone — abandoning it is what unblocks Finalize's Inv-7
        // guard, so doing it here would re-open the door this phase closes.
        _attemptRepo.Verify(a => a.UpdateAsync(It.IsAny<WorkflowAttempt>(), It.IsAny<CancellationToken>()), Times.Never);

        _adapter.Verify(a => a.CreateBookingAsync(It.IsAny<ProviderBookingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The Created-with-null-reference hole, closed by the same whitelist. `effectProven` requires
    /// a non-null reference, so this result proved neither branch — and fell into re-create.
    /// </summary>
    [Fact]
    public async Task Handle_CreatedWithNullReference_StaysReconciling_AndDoesNotRecreate()
    {
        var (tenantId, opId) = ("t-nullref", "op-created-nullref");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 1));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Dispatching, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Created));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Held,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The unresolved path must still self-terminate. Budget exhaustion is probed BEFORE the
    /// unresolved gate, so an operation nobody can resolve reaches a human rather than parking
    /// in Reconciling forever.
    /// </summary>
    [Fact]
    public async Task Handle_Unresolved_WithBudgetExhausted_StillEscalatesToAHuman()
    {
        var (tenantId, opId) = ("t-unres-exh", "op-unresolved-exhausted");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 9));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.Transient));

        // Budget exhausted.
        _retryPolicy.Setup(r => r.NextEligibleAt(
                It.IsAny<StepRetryBudget>(), It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .Returns((DateTimeOffset?)null);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.ManualReview,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.ManualReview,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }


    // ── #59: reconcile must settle first-party capacity before claiming Booked ──────────────

    /// <summary>
    /// The ledger half of the Booked write-set. Without this call the hold stays Active under a
    /// terminal Booked operation; ReleaseExpiredHolds later sweeps it and decrements
    /// HeldCapacity while BookedCapacity was never incremented, so the seat reads FREE while the
    /// booking exists (Inv-6).
    ///
    /// <para>Asserts the step key too. Capacity holds are keyed "create", not this handler's
    /// StepType of "finalize" — passing the wrong one finds no hold, returns NotTracked, and
    /// reproduces the bug while looking correct.</para>
    /// </summary>
    [Fact]
    public async Task Handle_EffectFound_SettlesCapacityAgainstTheCreateStep_BeforeBooking()
    {
        var (tenantId, opId) = ("t-cap", "op-cap-finalize");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 3));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, "ext_ref_cap"));

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        _capacityReservation.Verify(c => c.FinalizeAgainstOperationAsync(
            tenantId, opId, "create", It.IsAny<CancellationToken>()), Times.Once);

        // And never against this handler's own step type.
        _capacityReservation.Verify(c => c.FinalizeAgainstOperationAsync(
            It.IsAny<string>(), It.IsAny<string>(), "finalize", It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The hold is gone but the provider holds a booking — there is no capacity backing it.
    /// Finalize routes this to CompensationRequired; that edge does not exist from Reconciling,
    /// so Q1 chose ManualReview. Booking anyway would be the Inv-6 violation.
    /// </summary>
    [Fact]
    public async Task Handle_EffectFound_HoldNoLongerValid_EscalatesInsteadOfBooking()
    {
        var (tenantId, opId) = ("t-cap-gone", "op-hold-gone");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 5));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));

        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, "ext_ref_gone"));

        _capacityReservation.Setup(c => c.FinalizeAgainstOperationAsync(
                tenantId, opId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinalizeOutcome.HoldNoLongerValid);

        // Both destinations succeed if reached, so a wrong route is observable.
        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, It.IsAny<OperationStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Wait, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.ManualReview,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Booked,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // No confirmation may be emitted for a booking with no capacity behind it (Inv-16).
        _outboxRepo.Verify(r => r.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType.Contains("confirmed")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A non-tracked operation (no hold was ever created) still books — capacity settlement is a
    /// gate, not a requirement.
    /// </summary>
    [Fact]
    public async Task Handle_EffectFound_NotTracked_StillBooks()
    {
        var (tenantId, opId) = ("t-cap-untracked", "op-untracked");
        var providerKey = ProviderIdempotencyKey.Derive(opId, "finalize", 1);

        _opRepo.Setup(r => r.GetAsync(opId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOp(tenantId, opId, OperationStatus.Reconciling, version: 1));
        _stepRepo.Setup(r => r.GetByOperationAndTypeAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowStep?)null);
        _attemptRepo.Setup(r => r.GetLatestForStepAsync(tenantId, opId, "finalize", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAttempt(tenantId, opId, AttemptOutcome.Ambiguous, providerRef: null, providerKey: providerKey));
        _adapter.Setup(a => a.LookupByIdempotencyKeyAsync("default", providerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderResult(ProviderResultKind.AlreadyExists, "ext_ref_ut"));

        _capacityReservation.Setup(c => c.FinalizeAgainstOperationAsync(
                tenantId, opId, "create", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinalizeOutcome.NotTracked);

        _opRepo.Setup(r => r.TryTransitionStatusAsync(
                tenantId, opId, It.IsAny<long>(),
                OperationStatus.Reconciling, OperationStatus.Booked,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateHandler().Handle(
            new ReconcileOperationCommand(tenantId, opId), CancellationToken.None);

        Assert.Equal(StepOutcome.Terminal, result);

        _opRepo.Verify(r => r.TryTransitionStatusAsync(
            tenantId, opId, It.IsAny<long>(),
            OperationStatus.Reconciling, OperationStatus.Booked,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
