#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// The conditional dispatch write that fences a provider call against a concurrent cancel (#51).
///
/// <para><b>Why these are integration tests.</b> The guarantee lives entirely in one SQL
/// statement — <c>UPDATE workflow_attempts … WHERE outcome = 'planned' AND EXISTS (SELECT 1 FROM
/// booking_operations … AND status = 'finalizing')</c>. A mocked repository would assert that the
/// handler calls a method, which is not the same claim at all: the whole point is that the read
/// and the write are one statement, and only a database can demonstrate that.</para>
///
/// <para><b>What #51 actually was.</b> <c>FinalizeBookingCommandHandler</c> marked the attempt
/// Dispatching with an unconditional <c>UpdateAsync</c> returning <c>Task</c> — so it could not
/// learn that the operation had been cancelled underneath it, and called the provider anyway. The
/// issue proposed bumping a version there; that could not work, precisely because a void return
/// gives the caller nothing to observe.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class DispatchFenceTests(PostgresFixture fixture)
{
    private static WorkflowAttempt PlannedAttempt(string tenantId, string operationId) =>
        new()
        {
            TenantId = tenantId,
            AttemptId = $"att_{Guid.NewGuid():N}",
            OperationId = operationId,
            WorkflowStep = "finalize",
            ProviderIdempotencyKey = $"pik_{Guid.NewGuid():N}",
            ProviderName = "stub",
            ProviderAccount = "default",
            RequestHash = "h",
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Planned,
            RequestMeta = "{}",
            ResponseMeta = "{}",
        };

    private async Task<(string operationId, string attemptId)> SeedAsync(
        string tenantId, OperationStatus status, AttemptOutcome outcome = AttemptOutcome.Planned,
        bool cancellationRequested = false)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await TestData.SeedOperationAsync(ctx, tenantId, status);

        var attempt = PlannedAttempt(tenantId, operationId);
        attempt.Outcome = outcome;
        ctx.WorkflowAttempts.Add(attempt);
        await ctx.SaveChangesAsync();

        if (cancellationRequested)
        {
            await ctx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(o => o.CancellationRequestedAt, DateTimeOffset.UtcNow));
        }

        return (operationId, attempt.AttemptId);
    }

    private async Task<AttemptOutcome> ReadOutcomeAsync(string tenantId, string attemptId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.WorkflowAttempts.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(a => a.AttemptId == attemptId);
        return row.Outcome;
    }

    [Fact]
    public async Task ArmsTheDispatch_WhileTheOperationIsStillFinalizing()
    {
        var tenantId = $"fence-ok-{Guid.NewGuid():N}";
        var (operationId, attemptId) = await SeedAsync(tenantId, OperationStatus.Finalizing);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync(tenantId, operationId, attemptId);

        Assert.Equal(1, rows);
        Assert.Equal(AttemptOutcome.Dispatching, await ReadOutcomeAsync(tenantId, attemptId));
    }

    /// <summary>
    /// The #51 race, from the losing side. Once the cancel worker's CAS has moved the operation
    /// off Finalizing, this statement matches nothing and the provider is never called — where
    /// before, the dispatch went ahead and left a booking nothing would ever reference again.
    /// </summary>
    [Theory]
    [InlineData(OperationStatus.CompensationRequired)]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.Booked)]
    public async Task RefusesTheDispatch_OnceTheOperationHasLeftFinalizing(OperationStatus status)
    {
        var tenantId = $"fence-gone-{Guid.NewGuid():N}";
        var (operationId, attemptId) = await SeedAsync(tenantId, status);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync(tenantId, operationId, attemptId);

        Assert.Equal(0, rows);
        Assert.Equal(AttemptOutcome.Planned, await ReadOutcomeAsync(tenantId, attemptId));
    }

    /// <summary>Idempotent: an attempt already armed is not re-armed by a redelivery.</summary>
    [Fact]
    public async Task RefusesToReArmAnAttemptThatIsAlreadyDispatching()
    {
        var tenantId = $"fence-idem-{Guid.NewGuid():N}";
        var (operationId, attemptId) = await SeedAsync(
            tenantId, OperationStatus.Finalizing, outcome: AttemptOutcome.Dispatching);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync(tenantId, operationId, attemptId);

        Assert.Equal(0, rows);
    }

    /// <summary>
    /// A pending cancellation must NOT block the dispatch, and this pins that on purpose.
    ///
    /// <para>An earlier version of the fence also required <c>cancellation_requested_at IS NULL</c>,
    /// which broke <c>FinalizeDetectsCancelAfterCreate_CompensatesBooking_ReachesFailedFinal</c> —
    /// correctly. After a <c>TransientFailure</c> attempt the previous call may already have taken
    /// effect, and calling again under the same idempotency key is the only way to read back the
    /// <c>AlreadyExists</c> reference that Compensate then needs in order to cancel anything.
    /// FM#13 fast-paths a cancellation only for a <c>null</c>/<c>Planned</c> attempt for exactly
    /// this reason. Narrowing the fence back would strand the operation with nothing to cancel.</para>
    /// </summary>
    [Fact]
    public async Task StillArmsTheDispatch_WhenACancellationIsPending()
    {
        var tenantId = $"fence-cancel-{Guid.NewGuid():N}";
        var (operationId, attemptId) = await SeedAsync(
            tenantId, OperationStatus.Finalizing, cancellationRequested: true);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync(tenantId, operationId, attemptId);

        Assert.Equal(1, rows);
        Assert.Equal(AttemptOutcome.Dispatching, await ReadOutcomeAsync(tenantId, attemptId));
    }

    /// <summary>
    /// The attempt must belong to the operation whose status is being asserted.
    ///
    /// <para>The predicate checked <c>attempt_id</c> against one operation's status without ever
    /// tying the two together, so a mismatched pair would arm operation B's attempt on the
    /// strength of operation A being <c>Finalizing</c> — and the caller would then dispatch using
    /// B's idempotency key and request hash on A's behalf. Today's single call site always passes
    /// a matching pair; a conditional write should not depend on that.</para>
    /// </summary>
    [Fact]
    public async Task RefusesTheDispatch_WhenTheAttemptBelongsToAnotherOperation()
    {
        var tenantId = $"fence-xop-{Guid.NewGuid():N}";

        // Both operations are Finalizing, so only the attempt/operation link can refuse this.
        var (operationA, _) = await SeedAsync(tenantId, OperationStatus.Finalizing);
        var (_, attemptB) = await SeedAsync(tenantId, OperationStatus.Finalizing);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync(tenantId, operationA, attemptB);

        Assert.Equal(0, rows);
        Assert.Equal(AttemptOutcome.Planned, await ReadOutcomeAsync(tenantId, attemptB));
    }

    /// <summary>Tenant scope (Inv-13): another tenant's id cannot arm this attempt.</summary>
    [Fact]
    public async Task RefusesTheDispatch_ForAnotherTenant()
    {
        var tenantId = $"fence-tenant-{Guid.NewGuid():N}";
        var (operationId, attemptId) = await SeedAsync(tenantId, OperationStatus.Finalizing);

        await using var ctx = fixture.CreateContext(tenantId);
        var rows = await new WorkflowAttemptRepository(ctx)
            .TryMarkDispatchingAsync($"other-{Guid.NewGuid():N}", operationId, attemptId);

        Assert.Equal(0, rows);
        Assert.Equal(AttemptOutcome.Planned, await ReadOutcomeAsync(tenantId, attemptId));
    }
}
