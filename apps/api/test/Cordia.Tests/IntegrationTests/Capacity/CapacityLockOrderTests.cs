#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// The Cordia Capacity Lock Order (CLO), proven against real PostgreSQL rather than argued
/// (spec 07 §12: <i>"Alba integration tests against a real PostgreSQL for the concurrency/race
/// cases (deterministic two-transaction interleaving)"</i>).
///
/// <para>Everything here is deterministic by construction, never by timing:</para>
/// <list type="bullet">
///   <item><b>Deadlock tests</b> use a two-sided barrier so both transactions provably hold their
///     first row before either reaches for its second. PostgreSQL's deadlock detector then
///     <i>guarantees</i> it fires — the test cannot flake into a pass.</item>
///   <item><b>Blocking tests</b> set <c>SET LOCAL lock_timeout</c> so a genuine wait fails fast with
///     <c>55P03</c> instead of hanging the suite.</item>
/// </list>
///
/// <para>The negative controls matter as much as the positives. A test that only shows "the current
/// code does not deadlock" proves nothing if the scenario could never deadlock anyway — so each
/// safety proof is paired with a test that takes the SAME rows in the WRONG order and shows
/// PostgreSQL really does abort it with <c>40P01</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CapacityLockOrderTests(PostgresFixture fixture)
{
    private const string DeadlockSqlState = "40P01"; // deadlock_detected
    private const string LockNotAvailable = "55P03"; // lock_not_available

    /// <summary>The production primitive, bound to one context's connection.</summary>
    private static CapacityLockOrder OrderFor(ApplicationDbContext ctx)
        => new(new CapacityHoldRepository(ctx), new CapacitySlotRepository(ctx));

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    private async Task<string> GivenSlotAsync(string tenantId, string suffix, int total = 10, int booked = 2)
    {
        var slotId = $"slot_{suffix}_{TestIds.New("s")}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = "r1",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            TotalCapacity = total,
            HeldCapacity = 0,
            BookedCapacity = booked,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return slotId;
    }

    private async Task<string> GivenFinalizedHoldAsync(string tenantId, string operationId, string slotId)
    {
        var holdId = TestIds.New("hold");

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacityHolds.Add(new CapacityHold
        {
            TenantId = tenantId,
            HoldId = holdId,
            OperationId = operationId,
            WorkflowStep = "create",
            SlotId = slotId,
            ReservedCapacity = 2,
            Status = HoldStatus.Finalized,
            ReleaseReason = ReleaseReason.Finalized,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return holdId;
    }

    // ── Primitives ───────────────────────────────────────────────────────────────────────────

    private static Task LockSlotAsync(ApplicationDbContext ctx, string tenantId, string slotId)
        => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.capacity_slots WHERE tenant_id = {tenantId} AND slot_id = {slotId} FOR UPDATE");

    private static Task LockHoldAsync(ApplicationDbContext ctx, string tenantId, string holdId)
        => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.capacity_holds WHERE tenant_id = {tenantId} AND hold_id = {holdId} FOR UPDATE");

    private static PostgresException? AsPostgres(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is PostgresException pg)
                return pg;
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    var found = AsPostgres(inner);
                    if (found is not null)
                        return found;
                }
                return null;
            }
            ex = ex.InnerException;
        }
        return null;
    }

    /// <summary>
    /// Runs two transactions that each take <c>first</c>, wait until BOTH hold their first row, then
    /// reach for <c>second</c>. Returns the <c>PostgresException</c> if either side aborted.
    ///
    /// <para>The barrier is what makes a deadlock deterministic: without it the two could simply
    /// run one after the other and the test would pass for the wrong reason.</para>
    /// </summary>
    private async Task<PostgresException?> RaceAsync(
        string tenantId,
        (string First, string Second) sideA,
        (string First, string Second) sideB,
        Func<ApplicationDbContext, string, string, Task> lockFn)
    {
        var aHasFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bHasFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Side((string First, string Second) rows, TaskCompletionSource mine, TaskCompletionSource theirs)
        {
            await using var ctx = fixture.CreateContext(tenantId);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            try
            {
                await lockFn(ctx, tenantId, rows.First);
                mine.TrySetResult();
                await theirs.Task;
                await lockFn(ctx, tenantId, rows.Second);
                await tx.CommitAsync();
            }
            catch
            {
                // Release the peer so it can never hang if this side aborted before signalling.
                mine.TrySetResult();
                throw;
            }
        }

        var a = Side(sideA, aHasFirst, bHasFirst);
        var b = Side(sideB, bHasFirst, aHasFirst);

        try
        {
            await Task.WhenAll(a, b);
            return null;
        }
        catch
        {
            return AsPostgres(a.Exception) ?? AsPostgres(b.Exception);
        }
    }

    // ── 1. Negative controls: the cycles are real ────────────────────────────────────────────

    /// <summary>
    /// <b>The defect this work fixes.</b> Two cross-slot reschedules moving in opposite directions
    /// between the same pair of slots — "move 7pm→8pm" and "move 8pm→7pm" at the same moment.
    ///
    /// <para>Ordering by ROLE (new slot first, then old) makes each transaction take the rows in the
    /// opposite order from the other. Both rows are SLOTS, so locking holds before slots does
    /// nothing to prevent it. PostgreSQL aborts one side with <c>40P01</c>.</para>
    ///
    /// <para>This is the control for <see cref="TwoOpposingCrossSlotReschedulesDoNotDeadlock"/>:
    /// without it, that test would prove only that the scenario is harmless.</para>
    /// </summary>
    [Fact]
    public async Task OppositeOrderSlotAcquisition_Deadlocks_ProvingTheCycleIsReal()
    {
        var tenantId = TestIds.New("clo-cycle");
        var s1 = await GivenSlotAsync(tenantId, "a");
        var s2 = await GivenSlotAsync(tenantId, "b");

        var pg = await RaceAsync(tenantId, (s1, s2), (s2, s1), LockSlotAsync);

        Assert.NotNull(pg);
        Assert.Equal(DeadlockSqlState, pg!.SqlState);
    }

    /// <summary>
    /// The ORIGINAL defect, in the same style: a transaction taking the SLOT then the previous
    /// owner's HOLD, against one taking that HOLD then the SLOT. This is what modify-vs-cancel did
    /// before the hold-before-slot rule, and it still deadlocks when the order is inverted — which
    /// is why the rule is enforced rather than assumed.
    /// </summary>
    [Fact]
    public async Task SlotBeforeHoldAcquisition_Deadlocks_ProvingTheOriginalCycleIsReal()
    {
        var tenantId = TestIds.New("clo-hs");
        var slotId = await GivenSlotAsync(tenantId, "shared");
        var holdId = await GivenFinalizedHoldAsync(tenantId, "op_origin", slotId);

        // Side A takes the slot then the hold (the inversion); side B takes hold then slot (correct).
        var aHasFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bHasFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Side(bool slotFirst, TaskCompletionSource mine, TaskCompletionSource theirs)
        {
            await using var ctx = fixture.CreateContext(tenantId);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            try
            {
                if (slotFirst)
                    await LockSlotAsync(ctx, tenantId, slotId);
                else
                    await LockHoldAsync(ctx, tenantId, holdId);

                mine.TrySetResult();
                await theirs.Task;

                if (slotFirst)
                    await LockHoldAsync(ctx, tenantId, holdId);
                else
                    await LockSlotAsync(ctx, tenantId, slotId);

                await tx.CommitAsync();
            }
            catch
            {
                mine.TrySetResult();
                throw;
            }
        }

        var a = Side(true, aHasFirst, bHasFirst);
        var b = Side(false, bHasFirst, aHasFirst);

        PostgresException? pg = null;
        try
        {
            await Task.WhenAll(a, b);
        }
        catch
        {
            pg = AsPostgres(a.Exception) ?? AsPostgres(b.Exception);
        }

        Assert.NotNull(pg);
        Assert.Equal(DeadlockSqlState, pg!.SqlState);
    }

    // ── 2. The rule removes both cycles ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>The previous deadlock scenario cannot happen.</b> The same two opposing reschedules, but
    /// each naming both slots in one <see cref="CapacityLockOrder"/> acquisition — deliberately
    /// passing them in OPPOSITE input order, which is what the two lanes genuinely do (one sees
    /// <c>S1</c> as its old slot, the other sees it as its new one).
    ///
    /// <para>The primitive sorts, so both take <c>S1</c> then <c>S2</c>. One waits; neither aborts.
    /// Contrast <see cref="OppositeOrderSlotAcquisition_Deadlocks_ProvingTheCycleIsReal"/>, which is
    /// the identical scenario without the sort.</para>
    /// </summary>
    [Fact]
    public async Task TwoOpposingCrossSlotReschedulesDoNotDeadlock()
    {
        var tenantId = TestIds.New("clo-safe");
        var s1 = await GivenSlotAsync(tenantId, "a");
        var s2 = await GivenSlotAsync(tenantId, "b");

        // No barrier here, and that is on purpose: with a correct order the second transaction
        // BLOCKS on the first row, so it can never signal "I hold my first row". Waiting for that
        // signal would hang the test rather than prove anything.
        async Task Side(string[] slots)
        {
            await using var ctx = fixture.CreateContext(tenantId);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await OrderFor(ctx).AcquireAsync(tenantId, holdIds: [], slotIds: slots);
            await tx.CommitAsync();
        }

        var a = Side([s1, s2]);
        var b = Side([s2, s1]); // opposite input order — the primitive must normalise it

        var race = Task.WhenAll(a, b);
        var finished = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(finished, race), "The ordered acquisition blocked indefinitely.");
        await race; // rethrows if either side aborted
        Assert.Null(AsPostgres(a.Exception));
        Assert.Null(AsPostgres(b.Exception));
    }

    /// <summary>
    /// The ORIGINAL modify-vs-cancel scenario, likewise proven safe: both lanes acquire the previous
    /// owner's hold and the shared slot through the same primitive, so they enter through the same
    /// row and one waits instead of the pair cycling.
    /// </summary>
    [Fact]
    public async Task ModifyAndCancelAcquisitionsDoNotDeadlock()
    {
        var tenantId = TestIds.New("clo-mc");
        var slotId = await GivenSlotAsync(tenantId, "shared");
        var previousHold = await GivenFinalizedHoldAsync(tenantId, "op_origin", slotId);
        var ownHold = await GivenFinalizedHoldAsync(tenantId, "op_modify", slotId);

        async Task Side(string?[] holds)
        {
            await using var ctx = fixture.CreateContext(tenantId);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await OrderFor(ctx).AcquireAsync(tenantId, holdIds: holds, slotIds: [slotId]);
            await tx.CommitAsync();
        }

        // Modify names both holds; cancel names only the previous owner's. Shared entry row.
        var modify = Side([previousHold, ownHold]);
        var cancel = Side([previousHold]);

        var race = Task.WhenAll(modify, cancel);
        var finished = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(finished, race), "The ordered acquisition blocked indefinitely.");
        await race;
    }

    // ── 3. The locks are real, and they are taken in the stated order ────────────────────────

    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> on a SLOT genuinely holds the row. Without this the whole slot
    /// half of the rule would be decoration: a passing suite would only prove it does not throw.
    /// </summary>
    [Fact]
    public async Task SlotLockAsyncGenuinelyHoldsTheRowAgainstAConcurrentWriter()
    {
        var tenantId = TestIds.New("clo-slock");
        var slotId = await GivenSlotAsync(tenantId, "held");

        await using var holder = fixture.CreateContext(tenantId);
        await using var holderTx = await holder.Database.BeginTransactionAsync();
        await new CapacitySlotRepository(holder).LockAsync(tenantId, slotId);

        await using var contender = fixture.CreateContext(tenantId);
        await using var contenderTx = await contender.Database.BeginTransactionAsync();
        await contender.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms';");

        var blocked = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await contender.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.capacity_slots SET version = version + 1 WHERE tenant_id = {tenantId} AND slot_id = {slotId}"));

        Assert.Equal(LockNotAvailable, AsPostgres(blocked)?.SqlState);

        await contenderTx.RollbackAsync();
        await holderTx.RollbackAsync();
    }

    /// <summary>
    /// <b>Holds really are taken before slots.</b> A holder pins the HOLD row; a second transaction
    /// then runs the full ordered acquisition for that hold plus a slot.
    ///
    /// <para>It must fail at the HOLD and never reach the slot. That is asserted positively: after
    /// the blocked transaction is rolled back, a THIRD transaction updates the slot with a short
    /// <c>lock_timeout</c> and succeeds — which it could not do if the second had taken the slot
    /// first and were still holding it.</para>
    /// </summary>
    [Fact]
    public async Task AcquisitionBlocksOnTheHoldBeforeItEverReachesTheSlot()
    {
        var tenantId = TestIds.New("clo-hbs");
        var slotId = await GivenSlotAsync(tenantId, "guarded");
        var holdId = await GivenFinalizedHoldAsync(tenantId, "op_origin", slotId);

        await using var holder = fixture.CreateContext(tenantId);
        await using var holderTx = await holder.Database.BeginTransactionAsync();
        await new CapacityHoldRepository(holder).LockAsync(tenantId, holdId);

        await using (var blocked = fixture.CreateContext(tenantId))
        {
            await using var blockedTx = await blocked.Database.BeginTransactionAsync();
            await blocked.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms';");

            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await OrderFor(blocked).AcquireAsync(
                    tenantId, holdIds: [holdId], slotIds: [slotId]));

            Assert.Equal(LockNotAvailable, AsPostgres(ex)?.SqlState);
            await blockedTx.RollbackAsync();
        }

        // The slot was never taken by the blocked transaction — prove it, don't assume it.
        await using var prober = fixture.CreateContext(tenantId);
        await using var proberTx = await prober.Database.BeginTransactionAsync();
        await prober.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms';");

        var rows = await prober.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.capacity_slots SET version = version + 1 WHERE tenant_id = {tenantId} AND slot_id = {slotId}");

        Assert.Equal(1, rows);
        await proberTx.RollbackAsync();
        await holderTx.RollbackAsync();
    }

    // ── 4. Locks release when the transaction ends ───────────────────────────────────────────

    /// <summary>
    /// A COMMITTED acquisition releases every row it took. The fix must not trade a deadlock for a
    /// permanently stuck row, and "it releases eventually" is not something to take on trust.
    /// </summary>
    [Fact]
    public async Task LocksReleaseWhenTheTransactionCommits()
    {
        var tenantId = TestIds.New("clo-rel-c");
        var slotId = await GivenSlotAsync(tenantId, "rel");
        var holdId = await GivenFinalizedHoldAsync(tenantId, "op_origin", slotId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await OrderFor(ctx).AcquireAsync(tenantId, holdIds: [holdId], slotIds: [slotId]);
            await tx.CommitAsync();
        }

        await AssertBothRowsAreFreeAsync(tenantId, holdId, slotId);
    }

    /// <summary>The rollback direction — the path a redelivered failure actually takes.</summary>
    [Fact]
    public async Task LocksReleaseWhenTheTransactionRollsBack()
    {
        var tenantId = TestIds.New("clo-rel-r");
        var slotId = await GivenSlotAsync(tenantId, "rel");
        var holdId = await GivenFinalizedHoldAsync(tenantId, "op_origin", slotId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await OrderFor(ctx).AcquireAsync(tenantId, holdIds: [holdId], slotIds: [slotId]);
            await tx.RollbackAsync();
        }

        await AssertBothRowsAreFreeAsync(tenantId, holdId, slotId);
    }

    private async Task AssertBothRowsAreFreeAsync(string tenantId, string holdId, string slotId)
    {
        await using var after = fixture.CreateContext(tenantId);
        await using var tx = await after.Database.BeginTransactionAsync();

        // A short lock_timeout turns "still locked" into a fast, explicit failure rather than a hang.
        await after.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms';");

        var slotRows = await after.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.capacity_slots SET version = version + 1 WHERE tenant_id = {tenantId} AND slot_id = {slotId}");
        var holdRows = await after.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.capacity_holds SET version = version + 1 WHERE tenant_id = {tenantId} AND hold_id = {holdId}");

        Assert.Equal(1, slotRows);
        Assert.Equal(1, holdRows);
        await tx.RollbackAsync();
    }

    // ── 5. The primitive's own contract ──────────────────────────────────────────────────────

    /// <summary>
    /// Null and blank ids are skipped rather than producing a lock on nothing, and duplicates
    /// collapse. Call sites pass optional ids straight through
    /// (<c>[previousHold?.HoldId, ownHold?.HoldId]</c>), so this is the shape they rely on.
    /// </summary>
    [Fact]
    public async Task AcquisitionSkipsNullsAndCollapsesDuplicates()
    {
        var tenantId = TestIds.New("clo-null");
        var slotId = await GivenSlotAsync(tenantId, "n");

        await using var ctx = fixture.CreateContext(tenantId);
        await using var tx = await ctx.Database.BeginTransactionAsync();

        await OrderFor(ctx).AcquireAsync(
            tenantId,
            holdIds: [null, "", null],
            slotIds: [slotId, slotId, null]);

        await tx.RollbackAsync();
    }
}
