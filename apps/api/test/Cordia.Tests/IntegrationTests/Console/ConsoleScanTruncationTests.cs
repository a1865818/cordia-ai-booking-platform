#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Console;

/// <summary>
/// The console's scan ceiling, at the one row count where "stopped early" and "finished" look
/// identical from inside the loop.
///
/// <para><c>ScanAsync</c> pages until it exhausts the tenant or reaches <c>MaxScan</c>, and it
/// used to <c>return true</c> — truncated — the moment the loop fell out at the ceiling, without
/// asking whether anything was actually left. A tenant with exactly <c>MaxScan</c> rows, or one
/// whose final page lands precisely on the boundary, had been read in FULL and was still told its
/// list was partial: the schedule claimed the grid might be missing bookings, and the customers
/// search claimed it might be missing a guest who does not exist. "Possibly incomplete" is the one
/// answer an operator cannot act on, so it must not be given when the scan is provably done.</para>
///
/// <para>Both directions are pinned in a single test, on one seeded tenant, because the pair is
/// the actual contract: at the boundary → complete; one row past it → truncated. Asserting only
/// the first would pass against a scan that never reports truncation at all.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ConsoleScanTruncationTests(PostgresFixture fixture)
{
    /// <summary>
    /// Mirrors the private <c>ConsoleReadModel.MaxScan</c>. Deliberately duplicated rather than
    /// exposed: this behaviour only exists AT the ceiling, so a test that read the constant could
    /// not distinguish the fix from the bug if the constant were ever wrong. If MaxScan changes,
    /// this test fails loudly and is meant to.
    /// </summary>
    private const int MaxScan = 20_000;

    /// <summary>
    /// Seeds <paramref name="count"/> operations joined to a confirmed revision, in three bulk
    /// statements. 20,000 rows through EF change tracking would dominate the suite's runtime;
    /// <c>generate_series</c> makes the whole fixture a few statements.
    ///
    /// <para>The payload is <c>{}</c> on purpose — it carries no <c>starts_at</c>, so no row ever
    /// matches the schedule window below and the scan runs to its ceiling instead of stopping on
    /// a full page. That keeps this test about the ceiling and nothing else.</para>
    /// </summary>
    private static async Task SeedOperationsAsync(
        ApplicationDbContext ctx, string tenantId, int fromIndex, int toIndex, string payload = "{}")
    {
        // Passed as a PARAMETER rather than written inline: a literal `{}` inside an interpolated
        // raw string would have to be brace-escaped, and the escape is easy to get subtly wrong.
        var emptyPayload = payload;

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.booking_intents (tenant_id, intent_id, operation_type)
            SELECT {tenantId}, 'int-' || g, 'create'::operation_type_enum
            FROM generate_series({fromIndex}, {toIndex}) AS g
            """);

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.booking_intent_revisions
                (tenant_id, intent_id, revision, canonical_payload,
                 payload_hash, schema_version, tenant_policy_version)
            SELECT {tenantId}, 'int-' || g, 1, {emptyPayload}::jsonb, 'h', '1.0', '1.0'
            FROM generate_series({fromIndex}, {toIndex}) AS g
            """);

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.booking_operations
                (tenant_id, operation_id, intent_id, intent_revision,
                 operation_type, status, created_at, updated_at, version)
            SELECT {tenantId}, 'op-' || g, 'int-' || g, 1,
                   'create'::operation_type_enum, 'booked'::operation_status_enum,
                   now(), now(), 0
            FROM generate_series({fromIndex}, {toIndex}) AS g
            """);
    }

    /// <summary>
    /// Seconds allowed for this test's database work, replacing Npgsql's 30 s default.
    ///
    /// <para><b>The query is not slow; the runner is contended.</b> Measured with
    /// <c>EXPLAIN (ANALYZE, BUFFERS)</c> on the worst page — <c>OFFSET 19500 LIMIT 500</c> over a
    /// seeded 20,001-row tenant — the plan is a hash join of two sequential scans feeding one
    /// quicksort, <b>44 ms, 637 buffer hits, entirely from shared buffers</b>. Forty pages is
    /// under two seconds of database time. CI saw a single page exceed <b>30 s</b>: a ~680x gap,
    /// which is a stall, not a cost.
    ///
    /// <para><b>Three explanations were tested and discarded rather than assumed.</b>
    /// Stale planner statistics after the bulk load — adding <c>ANALYZE</c> on both seeded tables
    /// against a freshly dropped database changed nothing. The seeding itself — the timeout lands
    /// in <c>ScanAsync</c>, not the inserts. A missing index on
    /// <c>(tenant_id, updated_at DESC, operation_id)</c> — creating it left the plan
    /// <i>byte-identical</i>, because under a large <c>OFFSET</c> the planner must produce every
    /// preceding row anyway and correctly prefers one hash join over 20,000 index lookups into
    /// <c>booking_intent_revisions</c>. None of the three was shipped.</para>
    ///
    /// <para><b>So this raises a limit; it does not fix a defect, and there is no defect here to
    /// fix.</b> Npgsql's 30 s default was never a budget anyone chose for the only test in the
    /// suite that seeds 20,000 rows and reads them back across 80 round-trips while the rest of
    /// the suite competes for the same containerised Postgres on a two-core runner. The failure
    /// predates the wave-2 branch — it is on <c>main</c> at <c>aface13</c>. What this test asserts
    /// is the ceiling's <i>semantics</i>: at the boundary, complete; one row past it, truncated.
    /// Those must not depend on how busy the runner is.</para>
    ///
    /// <para><b>Since #110 the paging cost itself is gone.</b> The index above did nothing for
    /// <c>OFFSET</c> paging, as measured; it is what keyset paging needed. Each page now resumes
    /// with <c>(updated_at, operation_id) &lt; (last row)</c> over
    /// <c>(tenant_id, updated_at DESC, operation_id DESC)</c> — an index seek of 500 rows, ~2 ms on
    /// the same seeded tenant, however deep the page. The timeout stays because the SEEDING of
    /// 20,000 rows still competes for the runner.</para>
    /// </summary>
    private const int CommandTimeoutSeconds = 180;

    [Fact]
    public async Task ExactlyAtTheCeiling_IsComplete_AndOneRowPastIt_IsTruncated()
    {
        var tenantId = $"t-scan-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Database.SetCommandTimeout(CommandTimeoutSeconds);

        await SeedOperationsAsync(ctx, tenantId, 1, MaxScan);

        var model = new ConsoleReadModel(ctx);

        // A window nothing falls into: every payload is `{}`, so the scan examines all MaxScan
        // rows, matches none, and falls out of the loop at the ceiling — the exact state that
        // used to be reported as truncated.
        var from = DateTimeOffset.UtcNow.AddDays(3650);
        var to = from.AddDays(1);

        var atCeiling = await model.ListScheduleAsync(tenantId, from, to, limit: 50);

        Assert.Empty(atCeiling.Bookings);
        Assert.False(
            atCeiling.Truncated,
            "A tenant with exactly MaxScan rows has been read in full — reporting it as partial " +
            "tells the operator the grid may be missing bookings when it is not.");

        // One more row, and the same scan genuinely does leave something unexamined.
        await SeedOperationsAsync(ctx, tenantId, MaxScan + 1, MaxScan + 1);

        var pastCeiling = await model.ListScheduleAsync(tenantId, from, to, limit: 50);

        Assert.True(
            pastCeiling.Truncated,
            "One row beyond the ceiling really is unexamined, and silently presenting that as a " +
            "complete list is the failure the ceiling exists to disclose.");
    }

    /// <summary>
    /// #110 — keyset paging must neither skip nor repeat rows at a page boundary.
    ///
    /// <para>This is the case keyset paging gets wrong when the cursor is not the whole sort key.
    /// Every seeded row is inserted by one statement, so all of them share a single
    /// <c>updated_at</c>: a cursor on <c>updated_at</c> alone would stop after the first page (every
    /// remaining row compares equal, none "older"), and a cursor that treated equality as "after"
    /// would repeat rows. 1,250 rows is two full pages of 500 and a partial third, so two boundaries
    /// fall inside one tie.</para>
    /// </summary>
    [Fact]
    public async Task EveryRow_IsSeenExactlyOnce_AcrossPageBoundariesInsideATie()
    {
        const int rows = 1_250;
        var tenantId = $"t-scan-keyset-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Database.SetCommandTimeout(CommandTimeoutSeconds);

        await SeedOperationsAsync(ctx, tenantId, 1, rows, payload: """{"starts_at":"2031-03-04T10:00:00Z"}""");

        var from = new DateTimeOffset(2031, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var schedule = await new ConsoleReadModel(ctx).ListScheduleAsync(tenantId, from, from.AddDays(1), limit: 5_000);

        Assert.False(schedule.Truncated);
        var ids = schedule.Bookings.Select(b => b.OperationId).ToList();
        Assert.Equal(rows, ids.Count);
        Assert.Equal(rows, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
