using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.Reconcile;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="ReconcileOperationCommandHandler"/> — verifies the §16.2 lookup
/// order (Inv-7), effect-found path (Reconciling → Booked), no-effect path (Reconciling → Held),
/// budget-exhausted path (Reconciling → ManualReview via ManualReviewEscalator),
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

    // ManualReviewEscalator is a sealed class; wire it with a sub-set of mocked repos.
    private ManualReviewEscalator CreateEscalator() =>
        new(_opRepo.Object, new Mock<IOperationWorkItemRepository>().Object,
            _outboxRepo.Object, _auditRepo.Object);

    private ReconcileOperationCommandHandler CreateHandler() =>
        new(_opRepo.Object, _stepRepo.Object, _attemptRepo.Object,
            _outboxRepo.Object, _auditRepo.Object, _adapter.Object,
            _retryPolicy.Object, CreateEscalator(), _uow.Object, new Metrics());

    private static BookingOperation MakeOp(
        string tenantId, string operationId, OperationStatus status, long version = 0) =>
        new()
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = "int-1",
            IntentRevision = 1,
            Status = status,
            Version = version,
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
}
