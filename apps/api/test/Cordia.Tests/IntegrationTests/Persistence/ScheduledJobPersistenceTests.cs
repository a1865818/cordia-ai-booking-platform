#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ScheduledJobPersistenceTests(PostgresFixture fixture)
{
    // (1) Partial index ix_scheduled_jobs_due exists
    [Fact]
    public async Task PartialIndex_IxScheduledJobsDue_Exists()
    {
        await using var ctx = fixture.CreateContext("t3-sj-idx");
        var exists = await ctx.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS value FROM pg_indexes WHERE indexname = 'ix_scheduled_jobs_due'")
            .AnyAsync();
        Assert.True(exists);
    }

    // (2) ListDueAsync returns only Pending jobs with RunAfter <= now; Done job excluded
    [Fact]
    public async Task ListDueAsync_ReturnsPendingDueOnly()
    {
        var tenantId = $"t3-sj-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Pending job in the past → should be returned
            ctx.ScheduledJobs.Add(new ScheduledJob
            {
                TenantId = tenantId,
                JobId = $"job-{Guid.NewGuid():N}",
                JobType = "hold.expiry",
                RunAfter = now.AddMinutes(-5),
                DestinationQueue = "q-due",
                Status = ScheduledJobStatus.Pending,
                Payload = "{}",
            });
            // Done job in the past → should NOT be returned
            ctx.ScheduledJobs.Add(new ScheduledJob
            {
                TenantId = tenantId,
                JobId = $"job-{Guid.NewGuid():N}",
                JobType = "hold.expiry",
                RunAfter = now.AddMinutes(-10),
                DestinationQueue = "q-done",
                Status = ScheduledJobStatus.Done,
                Payload = "{}",
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var due = await readCtx.ScheduledJobs
            .AsNoTracking()
            .Where(j => j.Status == ScheduledJobStatus.Pending && j.RunAfter <= now)
            .OrderBy(j => j.RunAfter)
            .ToListAsync();

        Assert.Single(due);
        Assert.Equal(ScheduledJobStatus.Pending, due[0].Status);
        Assert.Equal("q-due", due[0].DestinationQueue);
    }
}
