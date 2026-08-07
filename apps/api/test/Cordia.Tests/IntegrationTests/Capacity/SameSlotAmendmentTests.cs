#pragma warning disable xUnit1051
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// An amendment that does not MOVE the booking (spec 07 §14) — the case release-then-reserve
/// cannot express.
///
/// <para>The slot key excludes party size, so a party-size change derives the slot the booking
/// already occupies. The two obvious implementations are each wrong in a different direction:</para>
///
/// <list type="bullet">
/// <item>Reserve the full new size → the slot must transiently carry the booking TWICE, so a slot
/// with no spare room answers NoCapacity and the guest is told there is no space for a table they
/// are sitting at.</item>
/// <item>Reserve only the difference and leave the previous hold → the booking ends up with TWO
/// finalized holds, and a later cancellation releases only the newest booked operation's, leaking
/// the rest of the seat permanently.</item>
/// </list>
///
/// <para><c>AbsorbSameSlotAsync</c> is the primitive that is neither: it moves the slot by exactly
/// the difference and leaves ONE finalized hold, owned by the amending operation. These tests pin
/// the arithmetic — including the end state a later cancellation depends on, which is where the
/// half-fixes came apart.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class SameSlotAmendmentTests(PostgresFixture fixture)
{
    private const string PreviousOp = "op_create_original";
    private const string NewOp = "op_modify";
    private const string Step = "create";

    /// <summary>Seeds a slot the booking already occupies with <paramref name="booked"/> units.</summary>
    private async Task<string> GivenBookedSlotAsync(
        string tenantId, int total, int booked, int held = 0)
    {
        var slotId = $"{TestIds.New("res")}|{DateTimeOffset.UtcNow.AddDays(1):O}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = "r1",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            TotalCapacity = total,
            HeldCapacity = held,
            BookedCapacity = booked,
            Version = 0,
        });
        ctx.CapacityHolds.Add(new CapacityHold
        {
            TenantId = tenantId,
            HoldId = TestIds.New("hold"),
            OperationId = PreviousOp,
            WorkflowStep = Step,
            SlotId = slotId,
            ReservedCapacity = booked,
            Status = HoldStatus.Finalized,
            ReleaseReason = ReleaseReason.Finalized,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return slotId;
    }

    private static FirstPartyCapacityReservation Reservation(Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx)
        => new(
            ctx,
            new CapacityHoldRepository(ctx),
            new CapacityLockOrder(new CapacityHoldRepository(ctx), new CapacitySlotRepository(ctx)),
            new Cordia.Infrastructure.Observability.Metrics());

    /// <summary>
    /// Growing the party on the same slot lands the slot on the NEW size — not the sum, and not
    /// the difference.
    /// </summary>
    [Fact]
    public async Task GrowingThePartyOnTheSameSlotEndsAtTheNewSize()
    {
        var tenantId = TestIds.New("cap-grow");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // The reserve step took only the difference (6 - 4 = 2) and finalized it.
            ctx.CapacityHolds.Add(new CapacityHold
            {
                TenantId = tenantId,
                HoldId = TestIds.New("hold"),
                OperationId = NewOp,
                WorkflowStep = Step,
                SlotId = slotId,
                ReservedCapacity = 2,
                Status = HoldStatus.Finalized,
                ReleaseReason = ReleaseReason.Finalized,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow,
                Version = 0,
            });
            await ctx.CapacitySlots
                .Where(s => s.TenantId == tenantId && s.SlotId == slotId)
                .ExecuteUpdateAsync(b => b.SetProperty(s => s.BookedCapacity, 6));
            await ctx.SaveChangesAsync();

            Assert.True(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 6));
        }

        await AssertSettledAsync(tenantId, slotId, expectedBooked: 6, expectedHoldCapacity: 6);
    }

    /// <summary>
    /// Shrinking gives the difference back. The reserve step took nothing (nothing new was
    /// needed), so the amending operation has no hold at all until the absorb creates one.
    /// </summary>
    [Fact]
    public async Task ShrinkingThePartyOnTheSameSlotGivesTheDifferenceBack()
    {
        var tenantId = TestIds.New("cap-shrink");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.True(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 2));
        }

        await AssertSettledAsync(tenantId, slotId, expectedBooked: 2, expectedHoldCapacity: 2);
    }

    /// <summary>
    /// A FULL slot can still take a same-size amendment. This is the case that used to answer
    /// NoCapacity and tell the guest there was no room for the table they were sitting at.
    /// </summary>
    [Fact]
    public async Task AnUnchangedPartyOnAFullSlotIsAbsorbedRatherThanRefused()
    {
        var tenantId = TestIds.New("cap-full");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 4, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.True(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 4));
        }

        await AssertSettledAsync(tenantId, slotId, expectedBooked: 4, expectedHoldCapacity: 4);
    }

    /// <summary>
    /// The primitive can raise the slot on its own, with no reserve step having pre-applied it.
    ///
    /// <para>Every other growth case here arrives with the delta already moved into `booked` by
    /// `FinalizeAgainstOperationAsync`, so `delta` computes to zero and the guarded slot UPDATE
    /// never runs. That left the one branch that actually INCREASES `BookedCapacity` proven only in
    /// its refusal form — a wrong sign or wrong column in the success path would have gone
    /// unnoticed. `AbsorbSameSlotAsync` is a public `ICapacityReservation` member, so it has to
    /// hold on its own terms and not only in the shape its current caller happens to produce.</para>
    /// </summary>
    [Fact]
    public async Task AbsorbRaisesTheSlotItselfWhenNothingWasReservedInAdvance()
    {
        var tenantId = TestIds.New("cap-raise");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // No hold for the amending operation at all, and no pre-applied booked units:
            // the absorb itself must move the slot 4 -> 6.
            Assert.True(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 6));
        }

        await AssertSettledAsync(tenantId, slotId, expectedBooked: 6, expectedHoldCapacity: 6);
    }

    /// <summary>
    /// The end state a later CANCELLATION depends on: releasing against the amending operation
    /// gives back the WHOLE seat.
    ///
    /// <para>This is the assertion the delta-only shape failed. Two finalized holds meant
    /// `GetBookedOperationForBookingAsync` resolved to the newest operation, its release gave back
    /// only the difference, and the original units stayed booked for a booking that no longer
    /// existed.</para>
    /// </summary>
    [Fact]
    public async Task AfterAnAmendmentACancellationGivesTheWholeSeatBack()
    {
        var tenantId = TestIds.New("cap-cancel");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 2);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await Reservation(ctx).ReleaseFinalizedByOperationAsync(
                tenantId, NewOp, ReleaseReason.ProviderReleased);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == tenantId && s.SlotId == slotId);

            Assert.Equal(0, slot.BookedCapacity);
            Assert.Equal(0, slot.HeldCapacity);
        }
    }

    /// <summary>Redelivery is a no-op: the previous hold is already Released, so nothing moves.</summary>
    [Fact]
    public async Task AbsorbingTwiceMovesTheSlotOnlyOnce()
    {
        var tenantId = TestIds.New("cap-idem");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 2);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.True(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 2));
        }

        await AssertSettledAsync(tenantId, slotId, expectedBooked: 2, expectedHoldCapacity: 2);
    }

    /// <summary>
    /// A slot that genuinely cannot take the increase refuses, and writes NOTHING — the booking
    /// keeps the seat it had rather than being left half-moved.
    /// </summary>
    [Fact]
    public async Task AnIncreaseTheSlotCannotTakeIsRefusedWithoutTouchingAnything()
    {
        var tenantId = TestIds.New("cap-refuse");
        var slotId = await GivenBookedSlotAsync(tenantId, total: 4, booked: 4);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.False(await Reservation(ctx).AbsorbSameSlotAsync(
                tenantId, PreviousOp, NewOp, Step, slotId, totalCapacity: 6));
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == tenantId && s.SlotId == slotId);
            Assert.Equal(4, slot.BookedCapacity);

            // The original owner still holds the seat.
            var previous = await ctx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.TenantId == tenantId && h.OperationId == PreviousOp);
            Assert.Equal(HoldStatus.Finalized, previous.Status);

            Assert.False(await ctx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(h => h.TenantId == tenantId && h.OperationId == NewOp));
        }
    }

    /// <summary>
    /// EXACTLY ONE finalized hold survives, owned by the amending operation, sized to the whole
    /// booking — the property every later path (cancel, compensation, another amendment) relies on.
    /// </summary>
    private async Task AssertSettledAsync(
        string tenantId, string slotId, int expectedBooked, int expectedHoldCapacity)
    {
        await using var ctx = fixture.CreateContext(tenantId);

        var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == tenantId && s.SlotId == slotId);
        Assert.Equal(expectedBooked, slot.BookedCapacity);
        Assert.Equal(0, slot.HeldCapacity);

        var finalized = await ctx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId
                        && h.SlotId == slotId
                        && h.Status == HoldStatus.Finalized)
            .ToListAsync();

        var only = Assert.Single(finalized);
        Assert.Equal(NewOp, only.OperationId);
        Assert.Equal(expectedHoldCapacity, only.ReservedCapacity);

        // And the slot's booked units equal what that single hold says the booking occupies.
        Assert.Equal(slot.BookedCapacity, only.ReservedCapacity);
    }

    // ── Lock ordering ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// `LockAsync` must genuinely take the row lock — the whole deadlock fix rests on it.
    ///
    /// <para>It issues <c>SELECT 1 … FOR UPDATE</c> through <c>ExecuteSqlInterpolatedAsync</c>,
    /// which is built for non-queries. That executes the statement server-side and discards the
    /// rows, so the lock IS taken — but "the tests pass" only proves it does not throw, not that it
    /// locks. This proves the lock by having a second transaction fail to take it.</para>
    ///
    /// <para>Deterministic rather than timing-based: the second transaction sets
    /// <c>lock_timeout</c>, so it fails fast with <c>55P03 lock_not_available</c> instead of
    /// hanging the suite if the lock is (correctly) held.</para>
    /// </summary>
    [Fact]
    public async Task LockAsyncGenuinelyHoldsTheRowAgainstAConcurrentWriter()
    {
        var tenantId = TestIds.New("cap-lock");
        await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        string holdId;
        await using (var read = fixture.CreateContext(tenantId))
        {
            holdId = (await read.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.TenantId == tenantId)).HoldId;
        }

        await using var holder = fixture.CreateContext(tenantId);
        await using var contender = fixture.CreateContext(tenantId);

        await using var holderTx = await holder.Database.BeginTransactionAsync();
        await new CapacityHoldRepository(holder).LockAsync(tenantId, holdId);

        await using var contenderTx = await contender.Database.BeginTransactionAsync();
        await contender.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms';");

        var blocked = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await contender.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.capacity_holds SET version = version + 1 WHERE tenant_id = {tenantId} AND hold_id = {holdId}"));

        // 55P03 lock_not_available — the contender could not get the row, which is only possible
        // if LockAsync is actually holding it.
        Assert.Contains("lock", blocked.ToString(), StringComparison.OrdinalIgnoreCase);

        await contenderTx.RollbackAsync();
        await holderTx.RollbackAsync();
    }

    /// <summary>
    /// The same lock must NOT be held once the holding transaction ends — otherwise the fix would
    /// trade a deadlock for a permanently stuck row.
    /// </summary>
    [Fact]
    public async Task LockAsyncReleasesTheRowWhenItsTransactionEnds()
    {
        var tenantId = TestIds.New("cap-lock-rel");
        await GivenBookedSlotAsync(tenantId, total: 10, booked: 4);

        string holdId;
        await using (var read = fixture.CreateContext(tenantId))
        {
            holdId = (await read.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.TenantId == tenantId)).HoldId;
        }

        await using (var holder = fixture.CreateContext(tenantId))
        await using (var holderTx = await holder.Database.BeginTransactionAsync())
        {
            await new CapacityHoldRepository(holder).LockAsync(tenantId, holdId);
            await holderTx.RollbackAsync();
        }

        // Now uncontended.
        await using var after = fixture.CreateContext(tenantId);
        await after.Database.ExecuteSqlRawAsync("SET lock_timeout = '750ms';");
        var rows = await after.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.capacity_holds SET version = version + 1 WHERE tenant_id = {tenantId} AND hold_id = {holdId}");

        Assert.Equal(1, rows);
    }
}
