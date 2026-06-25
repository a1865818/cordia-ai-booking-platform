#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.EntityFramework.Concurrency;
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
}
