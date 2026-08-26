#pragma warning disable xUnit1051
using System.Globalization;
using System.Reflection;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Pins <see cref="TenantScopedScheduledJobRepository.ClaimDueAsync"/> against the production claim
/// it stands in for (<c>Cordia.Infrastructure/Repositories/ScheduledJobRepository.cs</c>
/// <see cref="ScheduledJobRepository.ClaimDueAsync"/>). The scheduled-job twin of
/// <see cref="OutboxTenantScopeParityTests"/>; see that class for the full rationale.
///
/// <para>The double re-implements the production claim's raw SQL, lease bookkeeping and short
/// transaction, adding exactly one predicate (<c>tenant_id = {0}</c>). Nothing in the compiler
/// enforces "production plus one predicate": if the production claim gains a <c>WHERE</c>, changes
/// its <c>ORDER BY</c>, or writes another column, the copy keeps compiling and keeps passing while
/// no longer mirroring the path it exists to stand in for.</para>
///
/// <para><b>One extra thing is pinned here that the outbox twin has no equivalent for:</b> the claim
/// must NEVER touch <see cref="ScheduledJob.Attempt"/> (§15.2). Attempt is bumped only by
/// <c>RescheduleAsync</c>, because a lease-expiry reclaim has to redeliver the SAME
/// <c>message_id</c> (<c>sched-{jobId}-att{attempt}</c>) so the inbox dedups it. A double that
/// incremented Attempt would turn every reclaim into a new logical message and silently defeat
/// inbox deduplication in exactly the tests that exist to prove it works.</para>
///
/// <para><b>Determinism against a shared database.</b> The production claim is cross-tenant and
/// bounded (<c>ORDER BY run_after LIMIT n</c>), so this test stamps its rows with an
/// <see cref="AncientBase"/> <c>run_after</c> (year 2000) to make them the unambiguous head of the
/// claim window, asserts it saw no foreign row, and purges the reserved window before and after.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ScheduledJobTenantScopeParityTests(PostgresFixture fixture)
{
    /// <summary>Older than any real row this suite has ever written; see the class remarks.</summary>
    private static readonly DateTimeOffset AncientBase = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Upper bound of the reserved ancient window owned by this test class alone.</summary>
    private static readonly DateTimeOffset AncientCutoff = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Test 1: the double is the production claim, plus one predicate ───────────────

    [Fact]
    public async Task ScopedClaim_SelectsSameRowsAndWritesSameLeaseBookkeeping_AsProductionClaim()
    {
        var tenantId = $"schedparity-same-{Guid.NewGuid():N}";
        var leaseOwner = $"schedparity-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddSeconds(30);

        // A dataset that exercises every predicate of the claim: an unleased due row, a row whose
        // lease has expired (reclaimable), a row under a live lease (excluded), a row with prior
        // attempts (pins that Attempt is NOT touched), a terminal Done row and a terminal Dead row
        // (both excluded by the status predicate), a not-yet-due row (excluded by run_after), and a
        // spare eligible row that LIMIT must leave behind (pins LIMIT + ORDER BY).
        //
        // The job_id values descend as run_after ascends, ON PURPOSE. If they ascended together,
        // ORDER BY job_id and ORDER BY run_after would select the same rows and the fixture would
        // not pin the ordering at all — a drifted ORDER BY would sail through. Verified by mutation:
        // rewriting the double to ORDER BY job_id turns this test red only because of this.
        var specs = new[]
        {
            new RowSpec("z9-unleased", AncientBase, ScheduledJobStatus.Pending, 0, null, null),
            new RowSpec("y8-lease-expired", AncientBase.AddSeconds(1), ScheduledJobStatus.Claimed, 1, "stale-owner", now.AddSeconds(-60)),
            new RowSpec("x7-lease-live", AncientBase.AddSeconds(2), ScheduledJobStatus.Claimed, 1, "live-owner", now.AddSeconds(300)),
            new RowSpec("w6-prior-attempts", AncientBase.AddSeconds(3), ScheduledJobStatus.Pending, 2, null, null),
            new RowSpec("v5-done", AncientBase.AddSeconds(4), ScheduledJobStatus.Done, 1, null, null),
            new RowSpec("u4-dead", AncientBase.AddSeconds(5), ScheduledJobStatus.Dead, 1, null, null),
            new RowSpec("t3-not-yet-due", now.AddHours(6), ScheduledJobStatus.Pending, 0, null, null),
            new RowSpec("a0-beyond-limit", AncientBase.AddSeconds(7), ScheduledJobStatus.Pending, 0, null, null),
        };

        // Eligible in run_after order: z9, y8, w6, a0 — a limit of 3 must stop before a0.
        // Eligible in job_id order would be: a0, w6, y8 — a different set, which is the point.
        const int limit = 3;

        try
        {
            await PurgeAncientAsync();
            await SeedAsync(tenantId, specs);

            await using var productionCtx = fixture.CreateContext("schedparity-worker");
            var productionClaimed = await new ScheduledJobRepository(productionCtx)
                .ClaimDueAsync(limit, leaseOwner, leaseExpiresAt, now);
            AssertNoForeignRows(productionClaimed, tenantId);
            var productionRows = await SnapshotAsync(tenantId);

            // Restore the dataset byte-identically so the double runs against the same rows, not
            // merely a similar-looking copy.
            await DeleteAsync(tenantId);
            await SeedAsync(tenantId, specs);

            await using var scopedCtx = fixture.CreateContext("schedparity-worker");
            var scopedClaimed = await new TenantScopedScheduledJobRepository(
                    scopedCtx, new ScheduledJobRepository(scopedCtx), tenantId)
                .ClaimDueAsync(limit, leaseOwner, leaseExpiresAt, now);
            AssertNoForeignRows(scopedClaimed, tenantId);
            var scopedRows = await SnapshotAsync(tenantId);

            // Same rows selected.
            Assert.Equal(JobIdsOf(productionClaimed, tenantId), JobIdsOf(scopedClaimed, tenantId));

            // Identical persisted state — status, lease_owner, lease_expires_at, attempt and every
            // other column of every seeded row.
            Assert.Equal(productionRows, scopedRows);

            // Fixture-shape guard: the dataset is only a pin while the claim actually discriminates
            // over it. If BOTH implementations change together this is what notices.
            Assert.Equal(
                new[] { "w6-prior-attempts", "y8-lease-expired", "z9-unleased" },
                JobIdsOf(productionClaimed, tenantId));

            // §15.2: the claim must never bump Attempt — pinned explicitly because a double that
            // did would silently defeat inbox dedup on every lease-expiry reclaim.
            Assert.Equal(2, scopedClaimed.Single(j => j.JobId == "w6-prior-attempts").Attempt);
            Assert.All(scopedClaimed, j => Assert.Equal(ScheduledJobStatus.Claimed, j.Status));
        }
        finally
        {
            await DeleteAsync(tenantId);
        }
    }

    // ── Test 2: the one intended divergence — tenant scope ──────────────────────────

    [Fact]
    public async Task ProductionClaim_IsCrossTenant_WhereScopedClaim_SeesOnlyItsOwnTenant()
    {
        var tenantA = $"schedparity-a-{Guid.NewGuid():N}";
        var tenantB = $"schedparity-b-{Guid.NewGuid():N}";
        var leaseOwner = $"schedparity-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddSeconds(30);

        // Interleaved run_after, so a bounded claim cannot land on one tenant's rows by ordering
        // alone: whichever rows it returns, it chose them on the tenant predicate.
        var specsA = new[]
        {
            new RowSpec("a0", AncientBase, ScheduledJobStatus.Pending, 0, null, null),
            new RowSpec("a2", AncientBase.AddSeconds(2), ScheduledJobStatus.Pending, 0, null, null),
        };
        var specsB = new[]
        {
            new RowSpec("b1", AncientBase.AddSeconds(1), ScheduledJobStatus.Pending, 0, null, null),
            new RowSpec("b3", AncientBase.AddSeconds(3), ScheduledJobStatus.Pending, 0, null, null),
        };

        const int limit = 4;

        try
        {
            await PurgeAncientAsync();
            await SeedAsync(tenantA, specsA);
            await SeedAsync(tenantB, specsB);

            // Production: no tenant predicate — both tenants are claimed by one worker.
            await using var productionCtx = fixture.CreateContext("schedparity-worker");
            var productionClaimed = await new ScheduledJobRepository(productionCtx)
                .ClaimDueAsync(limit, leaseOwner, leaseExpiresAt, now);
            var productionTenants = productionClaimed.Select(j => j.TenantId).ToHashSet();
            Assert.Contains(tenantA, productionTenants);
            Assert.Contains(tenantB, productionTenants);

            // Same dataset again for the double.
            await DeleteAsync(tenantA);
            await DeleteAsync(tenantB);
            await SeedAsync(tenantA, specsA);
            await SeedAsync(tenantB, specsB);
            var untouchedB = await SnapshotAsync(tenantB);

            // Scoped: tenant A only, even at a limit that would have covered both.
            await using var scopedCtx = fixture.CreateContext("schedparity-worker");
            var scopedClaimed = await new TenantScopedScheduledJobRepository(
                    scopedCtx, new ScheduledJobRepository(scopedCtx), tenantA)
                .ClaimDueAsync(limit, leaseOwner, leaseExpiresAt, now);

            Assert.Equal(new[] { tenantA }, scopedClaimed.Select(j => j.TenantId).Distinct());
            Assert.Equal(new[] { "a0", "a2" }, JobIdsOf(scopedClaimed, tenantA));

            // Tenant B was neither dispatched nor even leased — the point of the scope.
            Assert.Equal(untouchedB, await SnapshotAsync(tenantB));
        }
        finally
        {
            await DeleteAsync(tenantA);
            await DeleteAsync(tenantB);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>One seeded scheduled job, reduced to the columns the claim keys on or writes.</summary>
    private sealed record RowSpec(
        string JobIdSuffix,
        DateTimeOffset RunAfter,
        ScheduledJobStatus Status,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAt);

    private static IEnumerable<string> JobIdsOf(
        IEnumerable<ScheduledJob> claimed, string tenantId) =>
        claimed
            .Where(j => j.TenantId == tenantId)
            .Select(j => j.JobId)
            .OrderBy(id => id, StringComparer.Ordinal);

    /// <summary>
    /// The ancient-<c>run_after</c> invariant, asserted rather than assumed: a bounded cross-tenant
    /// claim that reached a foreign row means this test's rows were not at the head of the claim
    /// window, and its comparison would be measuring the wrong thing.
    /// </summary>
    private static void AssertNoForeignRows(IEnumerable<ScheduledJob> claimed, string tenantId) =>
        Assert.All(
            claimed,
            j => Assert.True(
                j.TenantId == tenantId,
                $"Claim reached foreign tenant '{j.TenantId}' (job '{j.JobId}', "
                + $"run_after {j.RunAfter:O}). This test's rows are stamped year 2000 so they head "
                + "the ORDER BY run_after window; a foreign row inside the limit means older due "
                + "litter exists and the parity comparison is not deterministic."));

    private async Task SeedAsync(string tenantId, IEnumerable<RowSpec> specs)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        foreach (var spec in specs)
        {
            ctx.ScheduledJobs.Add(new ScheduledJob
            {
                TenantId = tenantId,
                JobId = spec.JobIdSuffix,
                // Unroutable type/queue: nothing in this suite can publish or dead-letter these.
                JobType = "test.schedparity.v1",
                DestinationQueue = "test.schedparity.queue",
                RunAfter = spec.RunAfter,
                Status = spec.Status,
                Attempt = spec.Attempt,
                LeaseOwner = spec.LeaseOwner,
                LeaseExpiresAt = spec.LeaseExpiresAt,
                Payload = "{}",
            });
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>Every column of every row of <paramref name="tenantId"/>, in a stable order.</summary>
    private async Task<IReadOnlyList<string>> SnapshotAsync(string tenantId)
    {
        await using var ctx = fixture.CreateContext("schedparity-worker");
        var rows = await ctx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId)
            .OrderBy(j => j.JobId)
            .ToListAsync();

        return rows.Select(Fingerprint).ToList();
    }

    /// <summary>
    /// Reflective over <see cref="ScheduledJob"/> on purpose: a column added to the entity and
    /// written by only one of the two claim bodies must fail this test without anyone extending it.
    /// </summary>
    private static string Fingerprint(ScheduledJob row) =>
        string.Join(
            "|",
            typeof(ScheduledJob)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={Render(p.GetValue(row))}"));

    private static string Render(object? value) => value switch
    {
        null => "<null>",
        DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>",
    };

    private async Task DeleteAsync(string tenantId)
    {
        await using var ctx = fixture.CreateContext("schedparity-worker");
        await ctx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Self-heals the reserved ancient window after a killed run: rows stamped year 2000 would
    /// otherwise sit at the head of every bounded cross-tenant claim forever. Nothing outside this
    /// class writes a <c>run_after</c> anywhere near it.
    /// </summary>
    private async Task PurgeAncientAsync()
    {
        await using var ctx = fixture.CreateContext("schedparity-worker");
        await ctx.ScheduledJobs
            .IgnoreQueryFilters()
            .Where(j => j.RunAfter < AncientCutoff)
            .ExecuteDeleteAsync();
    }
}
