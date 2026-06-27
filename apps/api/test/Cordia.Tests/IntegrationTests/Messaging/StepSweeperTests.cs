#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for <see cref="StepSweeperHostedService"/> against a real Postgres DB.
/// Covers §3/§5.1 (discovery), §6/§12.4 (idempotent re-enqueue via version-stamped MessageId),
/// §7 (safety-net, non-mutation), §11 (crash → sweeper re-enqueues).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class StepSweeperTests(PostgresFixture fixture)
{
    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Neutralizes all pre-existing pending/in_progress steps so that
    /// ListNonTerminalAsync (ORDER BY updated_at LIMIT N) deterministically reaches
    /// THIS test's freshly-seeded rows. The shared DB accumulates detritus across runs;
    /// if pending/in_progress steps exceed the batch limit a new test step is starved
    /// out of the claim window → flaky failure.
    /// Bumping UpdatedAt to now() (not deleting) keeps row counts stable and does not
    /// change status, so parallel collections are unaffected.
    /// </summary>
    private async Task NeutralizeExistingNonTerminalStepsAsync()
    {
        await using var ctx = fixture.CreateContext("step-sweeper-cleanup");
        await ctx.WorkflowSteps
            .IgnoreQueryFilters()
            .Where(s => s.Status == WorkflowStepStatus.Pending || s.Status == WorkflowStepStatus.InProgress)
            .ExecuteUpdateAsync(b => b
                .SetProperty(s => s.UpdatedAt, DateTimeOffset.UtcNow));
    }

    private StepSweeperHostedService BuildService(
        IMessageTransport transport,
        MessagingOptions? opts = null)
    {
        // Minimal ServiceProvider that resolves IWorkflowStepRepository and IMessageTransport
        // for the sweeper's per-step scopes.  DbContext is created via the fixture factory to
        // avoid re-registering all Npgsql enum mappings.
        var services = new ServiceCollection();

        services.AddTransient(_ => fixture.CreateContext("step-sweeper-worker"));
        services.AddTransient<IWorkflowStepRepository, WorkflowStepRepository>();
        // Transport is a singleton so the same fake instance is shared across all scopes.
        services.AddSingleton<IMessageTransport>(transport);

        var sp = services.BuildServiceProvider();
        var options = Options.Create(opts ?? new MessagingOptions
        {
            StepSweeper = new StepSweeperOptions
            {
                Enabled = true,
                StalenessSeconds = 60,
                BatchSize = 100,
            },
        });
        return new StepSweeperHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<StepSweeperHostedService>.Instance);
    }

    // ── Test 1: ListNonTerminalAsync discovers stale non-terminal steps cross-tenant ─

    [Fact]
    public async Task ListNonTerminalAsync_Discovers_StaleNonTerminalSteps_CrossTenant()
    {
        await NeutralizeExistingNonTerminalStepsAsync();

        var tenantA = $"t6-lt-a-{Guid.NewGuid():N}";
        var tenantB = $"t6-lt-b-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var staleTime = now.AddMinutes(-5);

        // ── Seed: two tenants each with a stale Pending step ────────────────────────
        string stepIdA;
        string stepIdB;
        await using (var ctx = fixture.CreateContext(tenantA))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantA);
            stepIdA = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantA,
                WorkflowStepId = stepIdA,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantB);
            stepIdB = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantB,
                WorkflowStepId = stepIdB,
                OperationId = opId,
                StepType = "payment",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });
            await ctx.SaveChangesAsync();
        }

        // ── Seed: InProgress stale step (should be returned) ────────────────────────
        var tenantC = $"t6-lt-c-{Guid.NewGuid():N}";
        string stepIdC;
        await using (var ctx = fixture.CreateContext(tenantC))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantC);
            stepIdC = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantC,
                WorkflowStepId = stepIdC,
                OperationId = opId,
                StepType = "finalize",
                Status = WorkflowStepStatus.InProgress,
                AttemptCount = 1,
                Version = 1,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });
            await ctx.SaveChangesAsync();
        }

        // ── Seed: Succeeded stale step (should NOT be returned) ─────────────────────
        var tenantD = $"t6-lt-d-{Guid.NewGuid():N}";
        string stepIdSucceeded;
        await using (var ctx = fixture.CreateContext(tenantD))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantD);
            stepIdSucceeded = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantD,
                WorkflowStepId = stepIdSucceeded,
                OperationId = opId,
                StepType = "finalize",
                Status = WorkflowStepStatus.Succeeded,
                AttemptCount = 1,
                Version = 2,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });
            await ctx.SaveChangesAsync();
        }

        // ── Seed: Terminal stale step (should NOT be returned) ──────────────────────
        var tenantE = $"t6-lt-e-{Guid.NewGuid():N}";
        string stepIdTerminal;
        await using (var ctx = fixture.CreateContext(tenantE))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantE);
            stepIdTerminal = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantE,
                WorkflowStepId = stepIdTerminal,
                OperationId = opId,
                StepType = "finalize",
                Status = WorkflowStepStatus.Terminal,
                AttemptCount = 1,
                Version = 3,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });
            await ctx.SaveChangesAsync();
        }

        // ── Seed: fresh Pending step UpdatedAt=now (should NOT be returned) ─────────
        var tenantF = $"t6-lt-f-{Guid.NewGuid():N}";
        string stepIdFresh;
        await using (var ctx = fixture.CreateContext(tenantF))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantF);
            stepIdFresh = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantF,
                WorkflowStepId = stepIdFresh,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = now,  // fresh — not stale
                CreatedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        // ── Seed: stale Pending step with NotBefore=now+1hr (should NOT be returned) ─
        var tenantG = $"t6-lt-g-{Guid.NewGuid():N}";
        string stepIdNotBefore;
        await using (var ctx = fixture.CreateContext(tenantG))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantG);
            stepIdNotBefore = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantG,
                WorkflowStepId = stepIdNotBefore,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
                NotBefore = now.AddHours(1),  // not yet ready
            });
            await ctx.SaveChangesAsync();
        }

        // ── Assert via repo directly ─────────────────────────────────────────────────
        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var repo = new WorkflowStepRepository(workerCtx);
        var staleBefore = now.AddSeconds(-60);
        var results = await repo.ListNonTerminalAsync(staleBefore, now, limit: 200);

        var returnedIds = results.Select(s => s.WorkflowStepId).ToHashSet();

        // Both cross-tenant stale Pending steps must be returned.
        Assert.Contains(stepIdA, returnedIds);
        Assert.Contains(stepIdB, returnedIds);

        // InProgress stale step must be returned.
        Assert.Contains(stepIdC, returnedIds);

        // Terminal statuses must NOT be returned.
        Assert.DoesNotContain(stepIdSucceeded, returnedIds);
        Assert.DoesNotContain(stepIdTerminal, returnedIds);

        // Fresh step (UpdatedAt=now) must NOT be returned.
        Assert.DoesNotContain(stepIdFresh, returnedIds);

        // NotBefore=future step must NOT be returned.
        Assert.DoesNotContain(stepIdNotBefore, returnedIds);
    }

    // ── Test 2: SweepOnceAsync re-enqueues a continuation per stale step (§11) ─────

    [Fact]
    public async Task SweepOnceAsync_ReEnqueues_Continuation_ForStalePendingStep()
    {
        await NeutralizeExistingNonTerminalStepsAsync();

        var tenantId = $"t6-sw-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var staleUpdatedAt = now.AddMinutes(-5);

        // Seed: operation + ConsumerInbox row (Admitted = "admitted but not completed")
        // + stale Pending WorkflowStep (Version=0).
        string operationId;
        string stepId;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

            // ConsumerInbox: admitted but not completed → §11 scenario.
            ctx.ConsumerInbox.Add(new ConsumerInbox
            {
                TenantId = tenantId,
                ConsumerName = "booking.finalizer",
                MessageId = $"msg-{Guid.NewGuid():N}",
                CorrelationId = operationId,
                OperationId = operationId,
                Outcome = InboxOutcome.Admitted,
                AdmittedAt = staleUpdatedAt,
                CompletedAt = null,
            });

            stepId = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = operationId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleUpdatedAt,
                CreatedAt = staleUpdatedAt,
            });

            await ctx.SaveChangesAsync();
        }

        // Fake transport that records published calls.
        var published = new List<(JobEnvelope Envelope, LogicalQueue Queue)>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(
                It.IsAny<JobEnvelope>(),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>(
                (e, q, _) => published.Add((e, q)))
            .Returns(Task.CompletedTask);

        var opts = new MessagingOptions
        {
            StepSweeper = new StepSweeperOptions
            {
                Enabled = true,
                StalenessSeconds = 60,
                BatchSize = 100,
            },
        };
        var service = BuildService(fakeTransport.Object, opts);
        var count = await service.SweepOnceAsync(CancellationToken.None);

        Assert.True(count >= 1, $"Expected at least 1 re-enqueued, got {count}");

        // Our specific step must appear in the published set.
        var expectedMessageId = $"sweep-{stepId}-v0";
        var match = published.FirstOrDefault(p => p.Envelope.MessageId == expectedMessageId);
        Assert.NotEqual(default, match);

        // Verify envelope fields (§11 / §12.2).
        Assert.Equal("booking.step.continue.v1", match.Envelope.MessageType);
        Assert.Equal(LogicalQueue.BookingCommands, match.Queue);
        Assert.Equal(tenantId, match.Envelope.TenantId);
        Assert.Equal(operationId, match.Envelope.OperationId);
        Assert.Equal(operationId, match.Envelope.CorrelationId);
        Assert.Equal(0, match.Envelope.AttemptHint);

        // Payload must reference the step by ID (§12.2).
        var payloadJson = match.Envelope.Payload.GetRawText();
        Assert.Contains(stepId, payloadJson);
        Assert.Contains(operationId, payloadJson);

        // Step must NOT have been mutated (re-read from DB).
        await using var freshCtx = fixture.CreateContext(tenantId);
        var reloaded = await freshCtx.WorkflowSteps
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.WorkflowStepId == stepId)
            .FirstAsync();
        Assert.Equal(WorkflowStepStatus.Pending, reloaded.Status);
        Assert.Equal(0L, reloaded.Version);
    }

    // ── Test 3: Idempotent re-enqueue — two sweeps of the same step → same MessageId ─

    [Fact]
    public async Task SweepOnceAsync_IdempotentReEnqueue_SameMessageId_BothSweeps()
    {
        await NeutralizeExistingNonTerminalStepsAsync();

        var tenantId = $"t6-idem-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var staleUpdatedAt = now.AddMinutes(-5);

        string stepId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantId);
            stepId = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleUpdatedAt,
                CreatedAt = staleUpdatedAt,
            });
            await ctx.SaveChangesAsync();
        }

        // Fake transport that records ALL envelopes across both sweeps.
        var allPublished = new List<(JobEnvelope Envelope, LogicalQueue Queue)>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(
                It.IsAny<JobEnvelope>(),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>(
                (e, q, _) => allPublished.Add((e, q)))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);

        // Run the sweeper TWICE for the same unchanged stale step.
        await service.SweepOnceAsync(CancellationToken.None);
        await service.SweepOnceAsync(CancellationToken.None);

        // Filter to envelopes referencing OUR specific step.
        var forOurStep = allPublished
            .Where(p => p.Envelope.MessageId.StartsWith($"sweep-{stepId}-"))
            .ToList();

        // Both sweeps must have produced an envelope for this step.
        Assert.True(forOurStep.Count >= 2,
            $"Expected ≥2 envelopes for step {stepId}, got {forOurStep.Count}");

        // ALL envelopes for the unchanged step must have the SAME MessageId (§6/§12.4):
        // same step + same Version → same MessageId → inbox would dedup to a single continuation.
        var distinctIds = forOurStep.Select(p => p.Envelope.MessageId).Distinct().ToList();
        Assert.Single(distinctIds);
        Assert.Equal($"sweep-{stepId}-v0", distinctIds[0]);
    }

    // ── Test 4: Terminal/fresh steps are not swept ───────────────────────────────────

    [Fact]
    public async Task SweepOnceAsync_DoesNotReEnqueue_Terminal_Or_Fresh_Steps()
    {
        await NeutralizeExistingNonTerminalStepsAsync();

        var tenantId = $"t6-excl-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var staleTime = now.AddMinutes(-5);

        string stepIdTerminal;
        string stepIdFresh;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantId);

            // Terminal step (stale) — must NOT be swept.
            stepIdTerminal = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepIdTerminal,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Terminal,
                AttemptCount = 2,
                Version = 4,
                UpdatedAt = staleTime,
                CreatedAt = staleTime,
            });

            // Fresh Pending step (UpdatedAt=now) — must NOT be swept.
            stepIdFresh = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepIdFresh,
                OperationId = opId,
                StepType = "payment",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = now,  // fresh — NOT stale
                CreatedAt = now,
            });

            await ctx.SaveChangesAsync();
        }

        var allPublished = new List<(JobEnvelope Envelope, LogicalQueue Queue)>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(
                It.IsAny<JobEnvelope>(),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>(
                (e, q, _) => allPublished.Add((e, q)))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);
        await service.SweepOnceAsync(CancellationToken.None);

        // Assert that no envelope references either excluded step.
        var terminalEnv = allPublished
            .Where(p => p.Envelope.Payload.GetRawText().Contains(stepIdTerminal))
            .ToList();
        var freshEnv = allPublished
            .Where(p => p.Envelope.Payload.GetRawText().Contains(stepIdFresh))
            .ToList();

        Assert.Empty(terminalEnv);
        Assert.Empty(freshEnv);
    }

    // ── Test 5: Advance → NEW MessageId (recovery never suppressed, §6/§12.4) ─────────
    // The other half of the idempotency invariant: when a step advances (its OCC Version
    // bumps) and stalls again, the version-stamped MessageId changes so the inbox does NOT
    // suppress the new continuation — recovery is never permanently blocked.

    [Fact]
    public async Task SweepOnceAsync_StepVersionAdvanced_ProducesNewMessageId()
    {
        await NeutralizeExistingNonTerminalStepsAsync();

        var tenantId = $"t6-adv-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var staleUpdatedAt = now.AddMinutes(-5);

        string stepId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var opId = await TestData.SeedOperationAsync(ctx, tenantId);
            stepId = $"ws-{Guid.NewGuid():N}";
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = opId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
                UpdatedAt = staleUpdatedAt,
                CreatedAt = staleUpdatedAt,
            });
            await ctx.SaveChangesAsync();
        }

        var allPublished = new List<JobEnvelope>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(
                It.IsAny<JobEnvelope>(),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>((e, _, _) => allPublished.Add(e))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);

        // First sweep at Version=0 → expect sweep-{stepId}-v0.
        await service.SweepOnceAsync(CancellationToken.None);

        // Simulate the step advancing then re-stalling: bump Version (the OCC helper always
        // bumps Version on a real transition) while keeping it stale + non-terminal.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await ctx.WorkflowSteps
                .IgnoreQueryFilters()
                .Where(s => s.WorkflowStepId == stepId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(s => s.Version, 1L)
                    .SetProperty(s => s.UpdatedAt, staleUpdatedAt));
        }

        // Second sweep at Version=1 → expect a DISTINCT sweep-{stepId}-v1.
        await service.SweepOnceAsync(CancellationToken.None);

        var idsForStep = allPublished
            .Where(e => e.MessageId.StartsWith($"sweep-{stepId}-"))
            .Select(e => e.MessageId)
            .Distinct()
            .ToList();

        Assert.Contains($"sweep-{stepId}-v0", idsForStep);
        Assert.Contains($"sweep-{stepId}-v1", idsForStep);
        Assert.Equal(2, idsForStep.Count);  // two distinct ids → recovery not suppressed
    }
}
