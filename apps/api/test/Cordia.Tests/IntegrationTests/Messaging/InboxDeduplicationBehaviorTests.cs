#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Behaviours;
using Cordia.Application.Messaging;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Tiny in-test request types ──────────────────────────────────────────────────────────

/// <summary>Queue-driven request used by the inbox deduplication tests.</summary>
file record InboxTestRequest(
    string ConsumerName,
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string? OperationId) : IRequest<Unit>, IInboxConsumerRequest;

/// <summary>Plain request that does NOT implement <see cref="IInboxConsumerRequest"/>
/// — used to verify the no-op passthrough path.</summary>
file record PlainRequest : IRequest<Unit>;

/// <summary>
/// Integration tests for <see cref="InboxDeduplicationBehavior{TRequest,TResponse}"/>.
///
/// Tests construct the behavior directly with a real <see cref="UnitOfWork"/> and
/// <see cref="InboxRepository"/> over the fixture's <see cref="ApplicationDbContext"/>,
/// and a stub <c>next</c> delegate — avoiding MediatR-pipeline scanning complexity while
/// proving Inv-5 (admit-once, duplicate-suppressed, rollback-no-partial-state).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class InboxDeduplicationBehaviorTests(PostgresFixture fixture)
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Seeds a BookingOperation and returns its operationId.
    /// Required because WorkflowStep has a FK to booking_operations.</summary>
    private async Task<string> SeedOperationAsync(string tenantId)
    {
        await using var seedCtx = fixture.CreateContext(tenantId);
        return await TestData.SeedOperationAsync(seedCtx, tenantId);
    }

    // ── Test 1: Admit once (happy path) ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_FirstDelivery_AdmitsRowAndCallsNextOnce()
    {
        var tenantId = $"t4-ib-a-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer.test";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var stepId = $"step-{Guid.NewGuid():N}";
        var operationId = await SeedOperationAsync(tenantId);
        var callCount = 0;

        await using var ctx = fixture.CreateContext(tenantId);
        var uow = new UnitOfWork(ctx);
        var inboxRepo = new InboxRepository(ctx);

        RequestHandlerDelegate<Unit> next = async ct =>
        {
            callCount++;
            // Write a durable WorkflowStep row inside the SAME transaction (Inv-5 §5.1).
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = operationId,
                StepType = "test.step.admit",
                Status = WorkflowStepStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync(ct);
            return Unit.Value;
        };

        var behavior = new InboxDeduplicationBehavior<InboxTestRequest, Unit>(uow, inboxRepo, new Metrics());
        var request = new InboxTestRequest(consumer, tenantId, messageId, null, null);

        var result = await behavior.Handle(request, next, CancellationToken.None);

        // next was called exactly once and returned the handler response.
        Assert.Equal(1, callCount);
        Assert.Equal(Unit.Value, result);

        // consumer_inbox row must exist with outcome=admitted.
        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.ConsumerInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConsumerName == consumer && e.MessageId == messageId);
        Assert.NotNull(row);
        Assert.Equal(InboxOutcome.Admitted, row!.Outcome);
        Assert.Equal(tenantId, row.TenantId);

        // The durable side-effect (WorkflowStep) must also be present.
        var step = await readCtx.WorkflowSteps
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.WorkflowStepId == stepId);
        Assert.NotNull(step);
    }

    // ── Test 2: Duplicate suppressed (Inv-5) ────────────────────────────────────────

    [Fact]
    public async Task Handle_DuplicateDelivery_DoesNotCallNextAndReturnsDefault()
    {
        var tenantId = $"t4-ib-b-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer.test";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var stepIdFirst = $"step-first-{Guid.NewGuid():N}";
        var stepIdSecond = $"step-second-{Guid.NewGuid():N}";
        var operationId = await SeedOperationAsync(tenantId);

        // ── First delivery ──────────────────────────────────────────────────────────
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var uow1 = new UnitOfWork(ctx1);
            var inboxRepo1 = new InboxRepository(ctx1);
            var callCount1 = 0;

            RequestHandlerDelegate<Unit> next1 = async ct =>
            {
                callCount1++;
                ctx1.WorkflowSteps.Add(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = stepIdFirst,
                    OperationId = operationId,
                    StepType = "test.step.dup-first",
                    Status = WorkflowStepStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await ctx1.SaveChangesAsync(ct);
                return Unit.Value;
            };

            var behavior1 = new InboxDeduplicationBehavior<InboxTestRequest, Unit>(uow1, inboxRepo1, new Metrics());
            var request = new InboxTestRequest(consumer, tenantId, messageId, null, null);
            await behavior1.Handle(request, next1, CancellationToken.None);
            Assert.Equal(1, callCount1);
        }

        // ── Second delivery (FRESH behavior + context — simulates redelivery) ───────
        var callCount2 = 0;
        Unit secondResult;

        await using (var ctx2 = fixture.CreateContext(tenantId))
        {
            var uow2 = new UnitOfWork(ctx2);
            var inboxRepo2 = new InboxRepository(ctx2);

            // Use a distinct StepType to avoid the uq_workflow_step_per_type unique constraint.
            RequestHandlerDelegate<Unit> next2 = async ct =>
            {
                callCount2++;
                ctx2.WorkflowSteps.Add(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = stepIdSecond, // distinct id to detect a second write
                    OperationId = operationId,
                    StepType = "test.step.dup-second",
                    Status = WorkflowStepStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await ctx2.SaveChangesAsync(ct);
                return Unit.Value;
            };

            var behavior2 = new InboxDeduplicationBehavior<InboxTestRequest, Unit>(uow2, inboxRepo2, new Metrics());
            var request = new InboxTestRequest(consumer, tenantId, messageId, null, null);
            secondResult = await behavior2.Handle(request, next2, CancellationToken.None);
        }

        // next must NOT have been called the second time.
        Assert.Equal(0, callCount2);
        // Ack-and-drop: returns default(Unit) without throwing.
        Assert.Equal(default(Unit), secondResult);

        // Exactly ONE consumer_inbox row exists.
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var rowCount = await verifyCtx.ConsumerInbox
            .AsNoTracking()
            .CountAsync(e => e.ConsumerName == consumer && e.MessageId == messageId);
        Assert.Equal(1, rowCount);

        // The SECOND durable side-effect was NOT written.
        var secondStep = await verifyCtx.WorkflowSteps
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.WorkflowStepId == stepIdSecond);
        Assert.Null(secondStep);
    }

    // ── Test 3: Handler failure → rollback, no partial state (§8) ───────────────────

    [Fact]
    public async Task Handle_HandlerThrows_RollsBackInboxRowAndSideEffect()
    {
        var tenantId = $"t4-ib-c-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer.test";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var stepId = $"step-fail-{Guid.NewGuid():N}";
        var operationId = await SeedOperationAsync(tenantId);

        await using var ctx = fixture.CreateContext(tenantId);
        var uow = new UnitOfWork(ctx);
        var inboxRepo = new InboxRepository(ctx);

        RequestHandlerDelegate<Unit> throwingNext = async ct =>
        {
            // Write a step row then throw — both must roll back together (§8).
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = operationId,
                StepType = "test.step.fail",
                Status = WorkflowStepStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync(ct);
            throw new InvalidOperationException("simulated handler failure");
        };

        var behavior = new InboxDeduplicationBehavior<InboxTestRequest, Unit>(uow, inboxRepo, new Metrics());
        var request = new InboxTestRequest(consumer, tenantId, messageId, null, null);

        // Exception must propagate so the broker can redeliver cleanly.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(request, throwingNext, CancellationToken.None));

        // NO consumer_inbox row — transaction was rolled back (§8).
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var row = await verifyCtx.ConsumerInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConsumerName == consumer && e.MessageId == messageId);
        Assert.Null(row);

        // NO WorkflowStep row — both writes were in one rolled-back txn.
        var step = await verifyCtx.WorkflowSteps
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.WorkflowStepId == stepId);
        Assert.Null(step);
    }

    // ── Test 4: Non-inbox request passthrough ────────────────────────────────────────

    [Fact]
    public async Task Handle_NonInboxRequest_PassesThroughWithoutInboxRow()
    {
        var tenantId = $"t4-ib-d-{Guid.NewGuid():N}";
        var callCount = 0;

        await using var ctx = fixture.CreateContext(tenantId);
        var uow = new UnitOfWork(ctx);
        var inboxRepo = new InboxRepository(ctx);

        RequestHandlerDelegate<Unit> next = _ =>
        {
            callCount++;
            return Task.FromResult(Unit.Value);
        };

        var behavior = new InboxDeduplicationBehavior<PlainRequest, Unit>(uow, inboxRepo, new Metrics());
        var result = await behavior.Handle(new PlainRequest(), next, CancellationToken.None);

        // next was called exactly once and returned the handler response.
        Assert.Equal(1, callCount);
        Assert.Equal(Unit.Value, result);

        // No consumer_inbox row was written.
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var rowCount = await verifyCtx.ConsumerInbox
            .AsNoTracking()
            .CountAsync(e => e.TenantId == tenantId);
        Assert.Equal(0, rowCount);
    }
}
