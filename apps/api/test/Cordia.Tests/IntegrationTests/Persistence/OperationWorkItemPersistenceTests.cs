#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OperationWorkItemPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TryUpdateState_ExactlyOneWins()
    {
        var tenantId = $"t2-wi-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var workItemId = $"wi-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OperationWorkItems.Add(new OperationWorkItem
            {
                TenantId = tenantId,
                WorkItemId = workItemId,
                OperationId = operationId,
                Reason = "requires_manual_review",
                State = "open",
                SlaDeadline = DateTimeOffset.UtcNow.AddHours(24),
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<OperationWorkItem>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.WorkItemId == workItemId,
            s => s.SetProperty(x => x.State, "in_progress"));

        var rows2 = await VersionTokenUpdate.UpdateAsync<OperationWorkItem>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.WorkItemId == workItemId,
            s => s.SetProperty(x => x.State, "in_progress"));

        Assert.Equal(1, rows1 + rows2);
        Assert.True(VersionTokenUpdate.ShouldReload(rows1) || VersionTokenUpdate.ShouldReload(rows2));

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.OperationWorkItems
            .AsNoTracking()
            .SingleAsync(w => w.WorkItemId == workItemId);

        Assert.Equal("in_progress", reloaded.State);
        Assert.Equal(1L, reloaded.Version);
    }

    // Status guard (§6.2) for the text-state variant: wrong expected state → 0, no mutation.
    [Fact]
    public async Task TryUpdateState_WrongExpectedState_ReturnsZero_AndDoesNotMutate()
    {
        var tenantId = $"t2-wig-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var workItemId = $"wi-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OperationWorkItems.Add(new OperationWorkItem
            {
                TenantId = tenantId,
                WorkItemId = workItemId,
                OperationId = operationId,
                Reason = "requires_manual_review",
                State = "open",
                SlaDeadline = DateTimeOffset.UtcNow.AddHours(24),
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Wrong expected state ("in_progress") with matching version → no-op.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new OperationWorkItemRepository(ctx);
            var rows = await repo.TryUpdateStateAsync(
                tenantId, workItemId, expectedVersion: 0,
                expectedState: "in_progress", nextState: "resolved", assignee: null);
            Assert.Equal(0, rows);
        }

        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var unchanged = await verifyCtx.OperationWorkItems.AsNoTracking()
                .SingleAsync(w => w.WorkItemId == workItemId);
            Assert.Equal("open", unchanged.State);
            Assert.Equal(0L, unchanged.Version);
        }

        // Correct expected state ("open") → transitions.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new OperationWorkItemRepository(ctx);
            var rows = await repo.TryUpdateStateAsync(
                tenantId, workItemId, expectedVersion: 0,
                expectedState: "open", nextState: "in_progress", assignee: "agent-1");
            Assert.Equal(1, rows);
        }

        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var moved = await verifyCtx.OperationWorkItems.AsNoTracking()
                .SingleAsync(w => w.WorkItemId == workItemId);
            Assert.Equal("in_progress", moved.State);
            Assert.Equal("agent-1", moved.Assignee);
            Assert.Equal(1L, moved.Version);
        }
    }
}
