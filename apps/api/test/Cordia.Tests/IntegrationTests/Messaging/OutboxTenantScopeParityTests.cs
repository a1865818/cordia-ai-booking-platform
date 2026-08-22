#pragma warning disable xUnit1051
using System.Globalization;
using System.Reflection;
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Pins <see cref="TenantScopedOutboxRepository.ClaimBatchAsync"/> against the production claim it
/// stands in for (<c>Cordia.Infrastructure/Repositories/OutboxRepository.cs</c>
/// <see cref="OutboxRepository.ClaimBatchAsync"/>).
///
/// <para><b>Why this exists.</b> The test double re-implements the production claim's raw SQL, lease
/// bookkeeping and short transaction, adding exactly one predicate (<c>tenant_id = {0}</c>). Its whole
/// justification is "production plus one predicate". Nothing in the compiler enforces that: if the
/// production claim gains a <c>WHERE</c> predicate, changes its <c>ORDER BY</c>, writes another column
/// or alters the lease/attempt arithmetic, the copy keeps compiling and keeps passing while no longer
/// mirroring the path it exists to stand in for. The five publisher-driving test classes would then be
/// exercising a fiction, and the flake fix would have quietly become a coverage hole.</para>
///
/// <para><b>What is pinned, and how.</b> Behaviour, not text — a textual SQL comparison would go red on
/// harmless reformatting and stay green on a behavioural change expressed differently. Both
/// implementations run against the <i>same</i> rows of a single tenant (the dataset is deleted and
/// re-seeded byte-identically between the two runs), and the test asserts they select the same
/// <c>message_id</c>s and leave <b>every</b> column of <b>every</b> seeded row identical. The row
/// comparison is reflective over <see cref="OutboxMessage"/>, so a column added to the entity — and to
/// only one of the two claim bodies — fails here without anyone remembering to extend this test.</para>
///
/// <para>The second test pins the one intended divergence: the production claim is cross-tenant
/// (spec §5.1) while the double is bounded to its tenant.</para>
///
/// <para><b>Determinism against a shared database.</b> The production claim is cross-tenant and
/// bounded (<c>ORDER BY occurred_at LIMIT n</c>), so a claim in a database full of other tenants'
/// unpublished rows would be non-deterministic. This test therefore stamps its rows with an
/// <see cref="AncientBase"/> <c>occurred_at</c> (year 2000) — no other test seeds anything but
/// <c>UtcNow</c> — which makes them the oldest rows in the table and so the unambiguous head of the
/// claim window. Every claim additionally asserts it saw no foreign row, so if that invariant ever
/// breaks the test says so instead of silently weakening. Rows are purged before and after each test
/// so the ancient stamp never becomes litter that starves a later run's bounded claim.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OutboxTenantScopeParityTests(PostgresFixture fixture)
{
    /// <summary>Older than any real row this suite has ever written; see the class remarks.</summary>
    private static readonly DateTimeOffset AncientBase = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Upper bound of the reserved ancient window owned by this test class alone.</summary>
    private static readonly DateTimeOffset AncientCutoff = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Test 1: the double is the production claim, plus one predicate ───────────────

    [Fact]
    public async Task ScopedClaim_SelectsSameRowsAndWritesSameLeaseBookkeeping_AsProductionClaim()
    {
        var tenantId = $"parity-same-{Guid.NewGuid():N}";
        var leaseOwner = $"parity-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddSeconds(30);

        // A dataset that exercises every predicate of the claim: an unleased row, a row whose lease
        // has expired (reclaimable), a row under a live lease (excluded), a row with prior attempts
        // (pins the increment), an already-published row (excluded), and a spare eligible row that
        // LIMIT must leave behind (pins LIMIT + ORDER BY).
        var specs = new[]
        {
            new RowSpec("p0-unleased", AncientBase, 0, null, null, null),
            new RowSpec("p1-lease-expired", AncientBase.AddSeconds(1), 1, "stale-owner", now.AddSeconds(-60), null),
            new RowSpec("p2-lease-live", AncientBase.AddSeconds(2), 1, "live-owner", now.AddSeconds(300), null),
            new RowSpec("p3-prior-attempts", AncientBase.AddSeconds(3), 2, null, null, null),
            new RowSpec("p4-published", AncientBase.AddSeconds(4), 1, null, null, now.AddSeconds(-120)),
            new RowSpec("p5-beyond-limit", AncientBase.AddSeconds(5), 0, null, null, null),
        };

        // Eligible in occurred_at order: p0, p1, p3, p5 — a limit of 3 must stop before p5.
        const int limit = 3;

        try
        {
            await PurgeAncientAsync();
            await SeedAsync(tenantId, specs);

            await using var productionCtx = fixture.CreateContext("parity-worker");
            var productionClaimed = await new OutboxRepository(productionCtx)
                .ClaimBatchAsync(limit, leaseOwner, leaseExpiresAt, now);
            AssertNoForeignRows(productionClaimed, tenantId);
            var productionRows = await SnapshotAsync(tenantId);

            // Restore the dataset byte-identically so the double runs against the same rows, not
            // merely a similar-looking copy.
            await DeleteAsync(tenantId);
            await SeedAsync(tenantId, specs);

            await using var scopedCtx = fixture.CreateContext("parity-worker");
            var scopedClaimed = await new TenantScopedOutboxRepository(
                    scopedCtx, new OutboxRepository(scopedCtx), tenantId)
                .ClaimBatchAsync(limit, leaseOwner, leaseExpiresAt, now);
            AssertNoForeignRows(scopedClaimed, tenantId);
            var scopedRows = await SnapshotAsync(tenantId);

            // The parity assertions come first so a drifted double is what the failure message
            // names, rather than the fixture-shape guard below tripping on the same cause.

            // Same rows selected.
            Assert.Equal(MessageIdsOf(productionClaimed, tenantId), MessageIdsOf(scopedClaimed, tenantId));

            // Identical persisted state — lease_owner, lease_expires_at, publish_attempts,
            // published_at and every other column of every seeded row.
            Assert.Equal(productionRows, scopedRows);

            // Fixture-shape guard: the dataset is only a pin while the claim actually discriminates
            // over it. If BOTH implementations change together this is what notices, and whoever
            // updates it has to decide deliberately that the new selection is correct.
            Assert.Equal(
                new[] { "p0-unleased", "p1-lease-expired", "p3-prior-attempts" },
                MessageIdsOf(productionClaimed, tenantId));
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
        var tenantA = $"parity-a-{Guid.NewGuid():N}";
        var tenantB = $"parity-b-{Guid.NewGuid():N}";
        var leaseOwner = $"parity-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddSeconds(30);

        // Interleaved occurred_at, so a bounded claim cannot land on one tenant's rows by ordering
        // alone: whichever rows it returns, it chose them on the tenant predicate.
        var specsA = new[]
        {
            new RowSpec("x0", AncientBase, 0, null, null, null),
            new RowSpec("x2", AncientBase.AddSeconds(2), 0, null, null, null),
        };
        var specsB = new[]
        {
            new RowSpec("y1", AncientBase.AddSeconds(1), 0, null, null, null),
            new RowSpec("y3", AncientBase.AddSeconds(3), 0, null, null, null),
        };

        const int limit = 4;

        try
        {
            await PurgeAncientAsync();
            await SeedAsync(tenantA, specsA);
            await SeedAsync(tenantB, specsB);

            // Production: no tenant predicate — both tenants are claimed by one worker.
            await using var productionCtx = fixture.CreateContext("parity-worker");
            var productionClaimed = await new OutboxRepository(productionCtx)
                .ClaimBatchAsync(limit, leaseOwner, leaseExpiresAt, now);
            var productionTenants = productionClaimed.Select(r => r.TenantId).ToHashSet();
            Assert.Contains(tenantA, productionTenants);
            Assert.Contains(tenantB, productionTenants);

            // Same dataset again for the double.
            await DeleteAsync(tenantA);
            await DeleteAsync(tenantB);
            await SeedAsync(tenantA, specsA);
            await SeedAsync(tenantB, specsB);
            var untouchedB = await SnapshotAsync(tenantB);

            // Scoped: tenant A only, even at a limit that would have covered both.
            await using var scopedCtx = fixture.CreateContext("parity-worker");
            var scopedClaimed = await new TenantScopedOutboxRepository(
                    scopedCtx, new OutboxRepository(scopedCtx), tenantA)
                .ClaimBatchAsync(limit, leaseOwner, leaseExpiresAt, now);

            Assert.Equal(new[] { tenantA }, scopedClaimed.Select(r => r.TenantId).Distinct());
            Assert.Equal(new[] { "x0", "x2" }, MessageIdsOf(scopedClaimed, tenantA));

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

    /// <summary>One seeded outbox row, reduced to the columns the claim keys on or writes.</summary>
    private sealed record RowSpec(
        string MessageIdSuffix,
        DateTimeOffset OccurredAt,
        int PublishAttempts,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAt,
        DateTimeOffset? PublishedAt);

    private static IEnumerable<string> MessageIdsOf(
        IEnumerable<OutboxMessage> claimed, string tenantId) =>
        claimed
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.MessageId)
            .OrderBy(id => id, StringComparer.Ordinal);

    /// <summary>
    /// The ancient-<c>occurred_at</c> invariant, asserted rather than assumed: a bounded cross-tenant
    /// claim that reached a foreign row means this test's rows were not at the head of the claim
    /// window, and its comparison would be measuring the wrong thing.
    /// </summary>
    private static void AssertNoForeignRows(IEnumerable<OutboxMessage> claimed, string tenantId) =>
        Assert.All(
            claimed,
            r => Assert.True(
                r.TenantId == tenantId,
                $"Claim reached foreign tenant '{r.TenantId}' (message '{r.MessageId}', "
                + $"occurred_at {r.OccurredAt:O}). This test's rows are stamped year 2000 so they "
                + "head the ORDER BY occurred_at window; a foreign row inside the limit means older "
                + "unpublished litter exists and the parity comparison is not deterministic."));

    private async Task SeedAsync(string tenantId, IEnumerable<RowSpec> specs)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        foreach (var spec in specs)
        {
            ctx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = spec.MessageIdSuffix,
                // Unroutable type: nothing in this suite can publish or dead-letter these rows.
                MessageType = "test.parity.v1",
                CorrelationId = $"corr-{spec.MessageIdSuffix}",
                Payload = "{}",
                OccurredAt = spec.OccurredAt,
                PublishAttempts = spec.PublishAttempts,
                LeaseOwner = spec.LeaseOwner,
                LeaseExpiresAt = spec.LeaseExpiresAt,
                PublishedAt = spec.PublishedAt,
            });
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>Every column of every row of <paramref name="tenantId"/>, in a stable order.</summary>
    private async Task<IReadOnlyList<string>> SnapshotAsync(string tenantId)
    {
        await using var ctx = fixture.CreateContext("parity-worker");
        var rows = await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.MessageId)
            .ToListAsync();

        return rows.Select(Fingerprint).ToList();
    }

    /// <summary>
    /// Reflective over <see cref="OutboxMessage"/> on purpose: a column added to the entity and
    /// written by only one of the two claim bodies must fail this test without anyone extending it.
    /// </summary>
    private static string Fingerprint(OutboxMessage row) =>
        string.Join(
            "|",
            typeof(OutboxMessage)
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
        await using var ctx = fixture.CreateContext("parity-worker");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Self-heals the reserved ancient window after a killed run: rows stamped year 2000 would
    /// otherwise sit at the head of every bounded cross-tenant claim forever. Nothing outside this
    /// class writes an <c>occurred_at</c> anywhere near it.
    /// </summary>
    private async Task PurgeAncientAsync()
    {
        await using var ctx = fixture.CreateContext("parity-worker");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.OccurredAt < AncientCutoff)
            .ExecuteDeleteAsync();
    }
}
