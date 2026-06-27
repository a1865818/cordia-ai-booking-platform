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
/// Integration tests for <see cref="ScheduledJobPollerHostedService"/> against a real Postgres DB.
/// Covers §5.1 (claim→publish→mark), §15.2 (attempt invariant), §11 (restart survival), §8 (leases).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ScheduledJobPollerTests(PostgresFixture fixture)
{
    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private ScheduledJob MakeJob(
        string tenantId,
        DateTimeOffset runAfter,
        ScheduledJobStatus status = ScheduledJobStatus.Pending,
        int attempt = 0,
        string destinationQueue = "booking.scheduled",
        string jobType = "hold.expiry",
        string? leaseOwner = null,
        DateTimeOffset? leaseExpiresAt = null)
    {
        return new ScheduledJob
        {
            TenantId = tenantId,
            JobId = $"job-{Guid.NewGuid():N}",
            JobType = jobType,
            RunAfter = runAfter,
            DestinationQueue = destinationQueue,
            Status = status,
            Attempt = attempt,
            Payload = "{}",
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
        };
    }

    private ScheduledJobPollerHostedService BuildService(
        IMessageTransport transport,
        MessagingOptions? opts = null)
    {
        // Build a minimal ServiceProvider that resolves IScheduledJobRepository and
        // IMessageTransport for the poller's per-job scopes. DbContext is created via
        // the fixture's factory to avoid re-registering all Npgsql enum mappings.
        var services = new ServiceCollection();

        services.AddTransient(_ => fixture.CreateContext("sched-poller-worker"));
        services.AddTransient<IScheduledJobRepository, ScheduledJobRepository>();
        // Transport is a singleton so the same fake instance is shared across all scopes.
        services.AddSingleton<IMessageTransport>(transport);

        var sp = services.BuildServiceProvider();
        var options = Options.Create(opts ?? new MessagingOptions
        {
            ScheduledJobPoller = new ScheduledJobPollerOptions { Enabled = true },
        });
        return new ScheduledJobPollerHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<ScheduledJobPollerHostedService>.Instance);
    }

    /// <summary>
    /// Neutralizes all pre-existing claimable jobs so ClaimDueAsync (ORDER BY run_after LIMIT N)
    /// deterministically reaches THIS test's freshly-seeded rows. The shared Cordia_API_Test DB
    /// accumulates detritus across runs; if pending/claimed jobs exceed the batch limit a new
    /// test job is starved out of the claim window → flaky failure.
    /// Marking done (not deleting) keeps row counts stable so parallel collections are unaffected.
    /// </summary>
    private async Task NeutralizeExistingClaimableAsync()
    {
        await using var ctx = fixture.CreateContext("sched-poller-cleanup");
        await ctx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == ScheduledJobStatus.Pending || j.Status == ScheduledJobStatus.Claimed)
            .ExecuteUpdateAsync(b => b
                .SetProperty(j => j.Status, ScheduledJobStatus.Done)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    // ── Test 1: ClaimDueAsync is cross-tenant, sets Status=Claimed+lease, does NOT change Attempt ──

    [Fact]
    public async Task ClaimDueAsync_SetsLease_IsCrossTenant_DoesNotChangeAttempt()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantA = $"t5-ca-{Guid.NewGuid():N}";
        var tenantB = $"t5-cb-{Guid.NewGuid():N}";
        var leaseOwner = $"test-poller-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExp = now.AddSeconds(30);

        ScheduledJob jobA;
        ScheduledJob jobB;

        // Seed one job per tenant, both with Attempt=2 (to verify it is preserved).
        await using (var ctx = fixture.CreateContext(tenantA))
        {
            jobA = MakeJob(tenantA, now.AddMinutes(-1), attempt: 2);
            ctx.ScheduledJobs.Add(jobA);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            jobB = MakeJob(tenantB, now.AddMinutes(-1), attempt: 2);
            ctx.ScheduledJobs.Add(jobB);
            await ctx.SaveChangesAsync();
        }

        // Claim cross-tenant via a context scoped to neither tenant.
        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var repo = new ScheduledJobRepository(workerCtx);
        var claimed = await repo.ClaimDueAsync(50, leaseOwner, leaseExp, now);

        // Both tenants' jobs must be claimed.
        var claimedIds = claimed.Select(j => j.JobId).ToHashSet();
        Assert.Contains(jobA.JobId, claimedIds);
        Assert.Contains(jobB.JobId, claimedIds);

        // Re-read in fresh contexts to confirm Status, lease, and Attempt were persisted correctly.
        await using var freshA = fixture.CreateContext(tenantA);
        var rowA = await freshA.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == jobA.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Claimed, rowA.Status);
        Assert.Equal(leaseOwner, rowA.LeaseOwner);
        Assert.NotNull(rowA.LeaseExpiresAt);
        // CRITICAL §15.2: Attempt must NOT have changed during claim.
        Assert.Equal(2, rowA.Attempt);

        await using var freshB = fixture.CreateContext(tenantB);
        var rowB = await freshB.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == jobB.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Claimed, rowB.Status);
        Assert.Equal(2, rowB.Attempt);
    }

    // ── Test 2: Does not claim not-yet-due or done/dead jobs ─────────────────────────

    [Fact]
    public async Task ClaimDueAsync_DoesNotClaim_NotYetDue_Or_DoneDeadJobs()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-nd-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob futureJob;
        ScheduledJob doneJob;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Future job: not yet due.
            futureJob = MakeJob(tenantId, now.AddHours(1), status: ScheduledJobStatus.Pending);
            // Done job: due but already done.
            doneJob = MakeJob(tenantId, now.AddMinutes(-1), status: ScheduledJobStatus.Done);
            ctx.ScheduledJobs.Add(futureJob);
            ctx.ScheduledJobs.Add(doneJob);
            await ctx.SaveChangesAsync();
        }

        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var repo = new ScheduledJobRepository(workerCtx);
        var claimed = await repo.ClaimDueAsync(50, "owner", now.AddSeconds(30), now);

        // Neither our future nor done job should appear in the claimed set.
        var claimedIds = claimed.Select(j => j.JobId).ToHashSet();
        Assert.DoesNotContain(futureJob.JobId, claimedIds);
        Assert.DoesNotContain(doneJob.JobId, claimedIds);
    }

    // ── Test 3: Reclaim — expired lease is reclaimable; live lease is not ────────────

    [Fact]
    public async Task ClaimDueAsync_ReclaimsExpiredLease_ExcludesLiveLease()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-rl-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob expiredLeaseJob;
        ScheduledJob liveLeasedJob;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Job with an expired lease: should be reclaimed.
            expiredLeaseJob = MakeJob(
                tenantId, now.AddMinutes(-5),
                status: ScheduledJobStatus.Claimed,
                leaseOwner: "old-owner",
                leaseExpiresAt: now.AddSeconds(-10));

            // Job with a live lease: must NOT be reclaimed.
            liveLeasedJob = MakeJob(
                tenantId, now.AddMinutes(-5),
                status: ScheduledJobStatus.Claimed,
                leaseOwner: "live-owner",
                leaseExpiresAt: now.AddSeconds(60));

            ctx.ScheduledJobs.Add(expiredLeaseJob);
            ctx.ScheduledJobs.Add(liveLeasedJob);
            await ctx.SaveChangesAsync();
        }

        var newOwner = $"reclaim-owner-{Guid.NewGuid():N}";
        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var repo = new ScheduledJobRepository(workerCtx);
        var claimed = await repo.ClaimDueAsync(50, newOwner, now.AddSeconds(30), now);

        var claimedIds = claimed.Select(j => j.JobId).ToHashSet();

        // Expired-lease job must be reclaimed.
        Assert.Contains(expiredLeaseJob.JobId, claimedIds);

        // Live-leased job must NOT be reclaimed.
        Assert.DoesNotContain(liveLeasedJob.JobId, claimedIds);

        // Verify the expired-lease job's owner has changed in the DB.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == expiredLeaseJob.JobId)
            .FirstAsync();
        Assert.Equal(newOwner, row.LeaseOwner);
    }

    // ── Test 4: SKIP LOCKED — two concurrent claims get different rows ────────────────

    [Fact]
    public async Task ClaimDueAsync_SkipLocked_TwoConcurrentClaimsDifferentRows()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-sl-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        string jobIdA;
        string jobIdB;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var jA = MakeJob(tenantId, now.AddMinutes(-1));
            var jB = MakeJob(tenantId, now.AddMinutes(-2));
            jobIdA = jA.JobId;
            jobIdB = jB.JobId;
            ctx.ScheduledJobs.Add(jA);
            ctx.ScheduledJobs.Add(jB);
            await ctx.SaveChangesAsync();
        }

        // Two concurrent raw transactions each claim 1 row — they must get different rows.
        await using var ctxA = fixture.CreateContext("worker");
        await using var txA = await ctxA.Database.BeginTransactionAsync();
        var rowsA = await ctxA.Set<ScheduledJob>()
            .FromSqlRaw(
                "SELECT * FROM public.scheduled_jobs " +
                "WHERE status::text <> 'done' AND status::text <> 'dead' " +
                "AND run_after <= {0} " +
                "AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY run_after LIMIT 1 FOR UPDATE SKIP LOCKED",
                now)
            .IgnoreQueryFilters()
            .ToListAsync();
        Assert.Single(rowsA);

        await using var ctxB = fixture.CreateContext("worker");
        await using var txB = await ctxB.Database.BeginTransactionAsync();
        var rowsB = await ctxB.Set<ScheduledJob>()
            .FromSqlRaw(
                "SELECT * FROM public.scheduled_jobs " +
                "WHERE status::text <> 'done' AND status::text <> 'dead' " +
                "AND run_after <= {0} " +
                "AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY run_after LIMIT 1 FOR UPDATE SKIP LOCKED",
                now)
            .IgnoreQueryFilters()
            .ToListAsync();
        Assert.Single(rowsB);

        // The two locked rows must be different.
        Assert.NotEqual(rowsA[0].JobId, rowsB[0].JobId);

        await txA.CommitAsync();
        await txB.CommitAsync();
    }

    // ── Test 5: Attempt + run_after survive worker restart (§11, §15.2) ───────────────

    [Fact]
    public async Task ClaimDueAsync_AttemptAndRunAfter_SurviveRestart_Unchanged()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-sr-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var originalRunAfter = now.AddMinutes(-5);

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            job = MakeJob(tenantId, originalRunAfter, attempt: 5);
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        // Simulate first claim (worker run).
        await using (var workerCtx = fixture.CreateContext("worker-tenant"))
        {
            var repo = new ScheduledJobRepository(workerCtx);
            var claimed = await repo.ClaimDueAsync(50, "owner-1", now.AddSeconds(30), now);
            Assert.Contains(claimed, j => j.JobId == job.JobId);
        }

        // Re-read in a FRESH context to verify §11 invariant: attempt and run_after unchanged.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();

        Assert.Equal(ScheduledJobStatus.Claimed, row.Status);
        // §11 / §15.2: claim must NOT mutate Attempt or RunAfter.
        Assert.Equal(5, row.Attempt);
        Assert.Equal(originalRunAfter, row.RunAfter);
    }

    // ── Test 6: RescheduleAsync increments Attempt, updates RunAfter, Status=Pending ──

    [Fact]
    public async Task RescheduleAsync_IncrementsAttempt_SetsNewRunAfter_ClearsLease()
    {
        var tenantId = $"t5-ra-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var newRunAfter = now.AddMinutes(10);

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            job = MakeJob(tenantId, now.AddMinutes(-1), attempt: 5,
                status: ScheduledJobStatus.Claimed,
                leaseOwner: "some-worker",
                leaseExpiresAt: now.AddSeconds(30));
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        await using (var workerCtx = fixture.CreateContext("worker-tenant"))
        {
            var repo = new ScheduledJobRepository(workerCtx);
            await repo.RescheduleAsync(tenantId, job.JobId, newRunAfter);
        }

        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();

        Assert.Equal(ScheduledJobStatus.Pending, row.Status);
        Assert.Equal(newRunAfter, row.RunAfter);
        Assert.Equal(6, row.Attempt);  // was 5, bumped by 1.
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);
    }

    // ── Test 7: MarkDoneAsync / MarkDeadAsync — set status, clear lease ───────────────

    [Fact]
    public async Task MarkDoneAsync_SetsStatusDone_ClearsLease_JobNotReclaimed()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-md-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            job = MakeJob(tenantId, now.AddMinutes(-1), status: ScheduledJobStatus.Claimed,
                leaseOwner: "owner", leaseExpiresAt: now.AddSeconds(30));
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        await using (var workerCtx = fixture.CreateContext("worker-tenant"))
        {
            var repo = new ScheduledJobRepository(workerCtx);
            await repo.MarkDoneAsync(tenantId, job.JobId);
        }

        // Verify Done + lease cleared.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Done, row.Status);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);

        // A subsequent claim must NOT return this job.
        await using var workerCtx2 = fixture.CreateContext("worker-tenant");
        var repo2 = new ScheduledJobRepository(workerCtx2);
        var reClaim = await repo2.ClaimDueAsync(50, "owner-2", now.AddSeconds(60), now.AddSeconds(60));
        Assert.DoesNotContain(reClaim, j => j.JobId == job.JobId);
    }

    [Fact]
    public async Task MarkDeadAsync_SetsStatusDead_ClearsLease_JobNotReclaimed()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-mda-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            job = MakeJob(tenantId, now.AddMinutes(-1), status: ScheduledJobStatus.Claimed,
                leaseOwner: "owner", leaseExpiresAt: now.AddSeconds(30));
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        await using (var workerCtx = fixture.CreateContext("worker-tenant"))
        {
            var repo = new ScheduledJobRepository(workerCtx);
            await repo.MarkDeadAsync(tenantId, job.JobId);
        }

        // Verify Dead + lease cleared.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Dead, row.Status);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);

        // A subsequent claim must NOT return this job.
        await using var workerCtx2 = fixture.CreateContext("worker-tenant");
        var repo2 = new ScheduledJobRepository(workerCtx2);
        var reClaim = await repo2.ClaimDueAsync(50, "owner-2", now.AddSeconds(60), now.AddSeconds(60));
        Assert.DoesNotContain(reClaim, j => j.JobId == job.JobId);
    }

    // ── Test 8: PollDueBatchAsync happy path ─────────────────────────────────────────

    [Fact]
    public async Task PollDueBatchAsync_HappyPath_PublishesEnvelope_MarksJobDone()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-hp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            job = MakeJob(tenantId, now.AddMinutes(-1),
                attempt: 3,
                destinationQueue: "booking.scheduled",
                jobType: "hold.expiry");
            ctx.ScheduledJobs.Add(job);
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
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>((e, q, _) => published.Add((e, q)))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);
        var count = await service.PollDueBatchAsync(CancellationToken.None);

        // At least 1 job was marked done (count >= 1; cross-tenant might pick up others).
        Assert.True(count >= 1, $"Expected at least 1 done, got {count}");

        // Our specific job must appear in the published set.
        var expectedMessageId = $"sched-{job.JobId}-att{job.Attempt}";
        Assert.Contains(published, p =>
            p.Envelope.MessageId == expectedMessageId &&
            p.Queue == LogicalQueue.BookingScheduled);

        // The envelope fields must be correct.
        var ourEnvelope = published.First(p => p.Envelope.MessageId == expectedMessageId).Envelope;
        Assert.Equal("hold.expiry", ourEnvelope.MessageType);
        Assert.Equal(tenantId, ourEnvelope.TenantId);
        Assert.Equal(job.Attempt, ourEnvelope.AttemptHint);

        // Verify the job is now Done in the DB.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Done, row.Status);
        Assert.Null(row.LeaseOwner);
    }

    // ── Test 9: unroutable destination_queue → marked Dead (no infinite reclaim) ─────

    [Fact]
    public async Task PollDueBatchAsync_UnknownDestinationQueue_MarksJobDead_NoPublish()
    {
        await NeutralizeExistingClaimableAsync();
        var tenantId = $"t5-bad-dest-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        ScheduledJob job;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // A destination_queue that maps to no logical queue — a permanent producer defect.
            job = MakeJob(tenantId, now.AddMinutes(-1), destinationQueue: "not.a.real.queue");
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var published = new List<JobEnvelope>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(
                It.IsAny<JobEnvelope>(),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>((e, _, _) => published.Add(e))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);
        await service.PollDueBatchAsync(CancellationToken.None);

        // The job is terminally Dead (stops the indefinite reclaim loop), lease cleared.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.JobId == job.JobId)
            .FirstAsync();
        Assert.Equal(ScheduledJobStatus.Dead, row.Status);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);

        // No envelope was published for the unroutable job.
        Assert.DoesNotContain(published, e => e.MessageId == $"sched-{job.JobId}-att{job.Attempt}");

        // A subsequent claim must NOT return it (it is Dead, excluded by status <> 'dead').
        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var reClaim = await new ScheduledJobRepository(workerCtx)
            .ClaimDueAsync(50, "owner-2", now.AddSeconds(60), now.AddSeconds(60));
        Assert.DoesNotContain(reClaim, j => j.JobId == job.JobId);
    }
}
