#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// <c>GetOrAddAsync</c> is the only way to create a workflow step, and these pin why.
///
/// <para>Every fulfilment handler creates its step while racing a redelivery or the step sweeper,
/// and <c>uq_workflow_step_per_type</c> makes a losing INSERT raise 23505. The handlers used
/// read-then-add, so the loser did not merely fail to reuse the winner's step — the violation
/// aborted the WHOLE ambient transaction, taking down the operation transition, the hold and the
/// outbox rows written beside it. These tests run the losing path against a real PostgreSQL,
/// because the failure only exists there: an in-memory double has no unique index and no
/// transaction to poison.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WorkflowStepCreateOrLoadTests(PostgresFixture fixture)
{
    private static async Task SeedOperationAsync(
        ApplicationDbContext ctx, string tenantId, string intentId, string operationId)
    {
        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Finalizing,
            Version = 0,
        });

        await ctx.SaveChangesAsync();
    }

    private static WorkflowStep Candidate(string tenantId, string operationId, string stepId)
        => new()
        {
            TenantId = tenantId,
            WorkflowStepId = stepId,
            OperationId = operationId,
            StepType = "finalize",
            Status = WorkflowStepStatus.InProgress,
            ProviderIdempotencyKey = operationId,
            AttemptCount = 1,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>First caller inserts, and gets back the row it proposed.</summary>
    [Fact]
    public async Task CreatesTheStep_WhenNoneExists()
    {
        var tenantId = $"t-ws-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedOperationAsync(ctx, tenantId, intentId, operationId);

        var repo = new WorkflowStepRepository(ctx);
        var created = await repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_first"));

        Assert.Equal("ws_first", created.WorkflowStepId);
        Assert.Equal(
            1,
            await ctx.WorkflowSteps.CountAsync(s => s.TenantId == tenantId && s.OperationId == operationId));
    }

    /// <summary>
    /// The losing caller gets the WINNER's row — not an exception, and not its own candidate.
    ///
    /// <para>This is the redelivery case: a second delivery proposes a different
    /// <c>workflow_step_id</c> for the same (tenant, operation, step_type). Returning the candidate
    /// would hand the caller a step id that does not exist, and every version-guarded transition
    /// against it would silently affect zero rows.</para>
    /// </summary>
    [Fact]
    public async Task ReturnsTheWinnersStep_WhenAnotherCallerAlreadyCreatedIt()
    {
        var tenantId = $"t-ws-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedOperationAsync(ctx, tenantId, intentId, operationId);

        var repo = new WorkflowStepRepository(ctx);
        await repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_winner"));

        var second = await repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_loser"));

        Assert.Equal("ws_winner", second.WorkflowStepId);
        Assert.Equal(
            1,
            await ctx.WorkflowSteps.CountAsync(s => s.TenantId == tenantId && s.OperationId == operationId));
    }

    /// <summary>
    /// A conflict whose winner cannot be READ must throw, never hand back the candidate.
    ///
    /// <para>Reached here by running the insert from a mis-scoped context: <c>WorkflowStep</c> is
    /// <c>ITenantScoped</c>, so the global query filter hides the winner from a context bound to a
    /// different tenant, while the raw INSERT — which is not filtered — still conflicts with it.
    /// That is the shape of any background scope whose <c>ITenantContext</c> is unset or wrong.</para>
    ///
    /// <para>Returning the candidate there would hand callers a <c>workflow_step_id</c> that exists
    /// nowhere, and they cannot tell: the finalize lane would call the provider, its version-guarded
    /// transition would affect zero rows and read as an ordinary lost race, and the retry scheduler
    /// would schedule against a phantom step — a booking created at the provider with the operation
    /// stranded in Finalizing. The read-then-add this replaced raised 23505 loudly; the replacement
    /// must not be quieter.</para>
    /// </summary>
    [Fact]
    public async Task Throws_WhenTheConflictWinnerCannotBeRead()
    {
        var tenantId = $"t-ws-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await SeedOperationAsync(ctx, tenantId, intentId, operationId);
            await new WorkflowStepRepository(ctx)
                .GetOrAddAsync(Candidate(tenantId, operationId, "ws_winner"));
        }

        // A context scoped to a DIFFERENT tenant: the filter hides the winner, the insert still
        // conflicts with it.
        await using var misScoped = fixture.CreateContext($"t-other-{Guid.NewGuid():N}");
        var repo = new WorkflowStepRepository(misScoped);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_loser")));

        Assert.Contains(operationId, thrown.Message);
    }

    /// <summary>
    /// The load-bearing one: losing the race must leave the ambient transaction USABLE.
    ///
    /// <para>This is what read-then-add could not do. The insert conflicts, and the transaction
    /// then goes on to write another row and COMMIT — the shape every caller has, where the step
    /// insert shares its transaction with an operation transition. Under the old code the 23505
    /// poisoned the transaction, so both the follow-up write and the commit failed with
    /// <c>current transaction is aborted</c> and the operation silently rolled back.</para>
    /// </summary>
    [Fact]
    public async Task LeavesTheTransactionUsable_WhenTheInsertConflicts()
    {
        var tenantId = $"t-ws-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedOperationAsync(ctx, tenantId, intentId, operationId);

        var repo = new WorkflowStepRepository(ctx);
        await repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_winner"));

        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            var loser = await repo.GetOrAddAsync(Candidate(tenantId, operationId, "ws_loser"));
            Assert.Equal("ws_winner", loser.WorkflowStepId);

            // The transaction must still be able to do work after the conflict.
            var rows = await repo.TryTransitionStatusAsync(
                tenantId, loser.WorkflowStepId, loser.Version,
                WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded);
            Assert.Equal(1, rows);

            await tx.CommitAsync();
        }

        var settled = await ctx.WorkflowSteps
            .AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.OperationId == operationId);

        Assert.Equal(WorkflowStepStatus.Succeeded, settled.Status);
    }
}
