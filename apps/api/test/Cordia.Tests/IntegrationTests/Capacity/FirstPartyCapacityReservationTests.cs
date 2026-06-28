#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Services;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// Integration tests for <see cref="FirstPartyCapacityReservation"/> (spec 07 §4.3, §4.4, §14.3).
/// Each test uses a unique tenantId so that rows inserted here are invisible to assertions in
/// other tests (the shared DB is scoped by tenant filter; no cleanup needed per test). All
/// tests instantiate the service with real EF contexts and wrap writes in explicit transactions
/// to mirror the UoW pattern used by the fulfillment handlers.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class FirstPartyCapacityReservationTests(PostgresFixture fixture)
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Creates a fresh service instance sharing the given DbContext.</summary>
    private static FirstPartyCapacityReservation MakeService(ApplicationDbContext ctx)
    {
        var holdRepo = new CapacityHoldRepository(ctx);
        return new FirstPartyCapacityReservation(ctx, holdRepo);
    }

    private static CapacitySlot MakeSlot(string tenantId, string slotId, int total, int held = 0, int booked = 0)
        => new()
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = $"res-{slotId}",
            StartsAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalCapacity = total,
            HeldCapacity = held,
            BookedCapacity = booked,
            Version = 0,
        };

    private static ReserveRequest MakeRequest(
        string tenantId,
        string operationId,
        string slotId,
        int capacity = 1,
        string workflowStep = "create")
        => new(
            tenantId,
            operationId,
            workflowStep,
            slotId,
            capacity,
            DateTimeOffset.UtcNow.AddMinutes(30),
            "first_party");

    /// <summary>
    /// Seeds a slot and seeds an operation; returns (slotId, operationId).
    /// The slot is inserted outside of any test transaction so it is immediately visible.
    /// </summary>
    private async Task<(string slotId, string operationId)> SeedSlotAndOperationAsync(
        string tenantId,
        int total)
    {
        var slotId = $"slot-{Guid.NewGuid():N}";
        string operationId;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacitySlots.Add(MakeSlot(tenantId, slotId, total));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        return (slotId, operationId);
    }

    // ── Test 1: Reserve success ──────────────────────────────────────────────

    [Fact]
    public async Task ReserveAsync_SlotHasCapacity_ReturnsHeld_AndIncrementsSlot()
    {
        var tenantId = $"t7-res-success-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 2);

        ReservationOutcome outcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            outcome = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            await tx.CommitAsync();
        }

        Assert.Equal(ReservationStatus.Held, outcome.Status);
        Assert.NotNull(outcome.Hold);
        Assert.Equal(HoldStatus.Active, outcome.Hold!.Status);
        Assert.Equal(1, outcome.Hold.ReservedCapacity);
        Assert.True(outcome.Hold.ExpiresAt > DateTimeOffset.UtcNow);

        // Verify slot increment persisted
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(1, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
        }

        // Verify hold row persisted
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var hold = await ctx.CapacityHolds.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == outcome.Hold!.HoldId);
            Assert.Equal(HoldStatus.Active, hold.Status);
            Assert.Equal(1, hold.ReservedCapacity);
            Assert.Equal(slotId, hold.SlotId);
        }
    }

    // ── Test 2: Last-unit / NoCapacity — slot never overcommitted ───────────

    [Fact]
    public async Task ReserveAsync_SlotFull_ReturnsNoCapacity_SlotUnchanged()
    {
        var tenantId = $"t7-res-nocap-{Guid.NewGuid():N}";
        var (slotId, opA) = await SeedSlotAndOperationAsync(tenantId, total: 1);
        string opB;
        await using (var ctx = fixture.CreateContext(tenantId))
            opB = await TestData.SeedOperationAsync(ctx, tenantId);

        // Operation A reserves the last unit
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, opA, slotId), default);
            Assert.Equal(ReservationStatus.Held, r.Status);
            await tx.CommitAsync();
        }

        // Operation B should see NoCapacity
        ReservationOutcome outcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            outcome = await svc.ReserveAsync(MakeRequest(tenantId, opB, slotId), default);
            await tx.CommitAsync();
        }

        Assert.Equal(ReservationStatus.NoCapacity, outcome.Status);
        Assert.Null(outcome.Hold);

        // Slot held must still be 1, NOT 2 (no overcommit)
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(1, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
        }
    }

    // ── Test 3: Idempotent re-reserve ────────────────────────────────────────

    [Fact]
    public async Task ReserveAsync_SameOpStepTwice_ReturnsExistingHold_SlotNotDoubleIncremented()
    {
        var tenantId = $"t7-res-idem-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 2);

        string firstHoldId;

        // First reserve
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            Assert.Equal(ReservationStatus.Held, r.Status);
            firstHoldId = r.Hold!.HoldId;
            await tx.CommitAsync();
        }

        // Second reserve (same op + step — duplicate delivery / re-drive)
        ReservationOutcome second;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            second = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            await tx.CommitAsync();
        }

        Assert.Equal(ReservationStatus.Held, second.Status);
        Assert.Equal(firstHoldId, second.Hold!.HoldId); // same hold, not a new one

        // Slot held must be 1, not 2 (no double increment)
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(1, slot.HeldCapacity);
        }
    }

    // ── Test 4: Release idempotency ──────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_ActiveHold_ReleasesOnce_SecondCallIsNoOp()
    {
        var tenantId = $"t7-rel-idem-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 1);

        // Reserve
        string holdId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            holdId = r.Hold!.HoldId;
            await tx.CommitAsync();
        }

        // First release
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            await svc.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, default);
            await tx.CommitAsync();
        }

        // Verify: hold = Expired, slot held = 0
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Expired, hold.Status);

            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
        }

        // Second release — must be a no-op (held stays 0, not negative)
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            await svc.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, default);
            await tx.CommitAsync();
        }

        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity); // must NOT go negative
        }
    }

    // ── Test 5: ReleaseByOperation ───────────────────────────────────────────

    [Fact]
    public async Task ReleaseByOperationAsync_ReleasesActiveHolds_IdempotentOnSecondCall()
    {
        var tenantId = $"t7-relop-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 2);

        // Reserve
        string holdId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            holdId = r.Hold!.HoldId;
            await tx.CommitAsync();
        }

        // First ReleaseByOperation
        int released1;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            released1 = await svc.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Compensated, default);
            await tx.CommitAsync();
        }

        Assert.Equal(1, released1);

        // Verify: hold released, slot freed
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Released, hold.Status);
            Assert.Equal(ReleaseReason.Compensated, hold.ReleaseReason);

            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
        }

        // Second call — must return 0 (idempotent)
        int released2;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            released2 = await svc.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Compensated, default);
            await tx.CommitAsync();
        }

        Assert.Equal(0, released2);
    }

    // ── Test 6: FinalizeAgainstOperation success (active→Finalized, slot held→booked) ──────────

    /// <summary>
    /// FinalizeAgainstOperationAsync with an active hold → Finalized, slot held=0 booked=1.
    /// The new op+step API is deterministic (no hold_id needed by caller).
    /// </summary>
    [Fact]
    public async Task FinalizeAgainstOperationAsync_ActiveHold_Finalizes_SlotHeldToBooked()
    {
        var tenantId = $"t7-fin-ok-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 2);

        // Reserve
        string holdId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            holdId = r.Hold!.HoldId;
            await tx.CommitAsync();
        }

        // Finalize via op+step (FIX 2+3+4+8)
        FinalizeOutcome finOutcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            finOutcome = await svc.FinalizeAgainstOperationAsync(tenantId, operationId, "create", default);
            await tx.CommitAsync();
        }

        Assert.Equal(FinalizeOutcome.Finalized, finOutcome);

        // Verify: hold = Finalized, slot held=0 booked=1
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Finalized, hold.Status);
            Assert.Equal(ReleaseReason.Finalized, hold.ReleaseReason);

            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(1, slot.BookedCapacity);
        }
    }

    // ── Test 6b: FinalizeAgainstOperation idempotent — already-Finalized hold returns Finalized, slot NOT moved again ──

    /// <summary>
    /// FIX 3 + FIX 8: calling FinalizeAgainstOperationAsync on a hold that is already Finalized
    /// must return Finalized (idempotent) WITHOUT moving the slot a second time.
    /// </summary>
    [Fact]
    public async Task FinalizeAgainstOperationAsync_AlreadyFinalizedHold_ReturnsFinalized_NoSecondSlotMove()
    {
        var tenantId = $"t7-fin-idem-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 1);

        // Reserve then Finalize the first time.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            await tx.CommitAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var outcome1 = await svc.FinalizeAgainstOperationAsync(tenantId, operationId, "create", default);
            Assert.Equal(FinalizeOutcome.Finalized, outcome1);
            await tx.CommitAsync();
        }

        // Verify slot state after first finalize.
        await using (var midCtx = fixture.CreateContext(tenantId))
        {
            var slot = await midCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(1, slot.BookedCapacity);
        }

        // Finalize again (idempotent retry) — must return Finalized, slot booked must still be 1.
        FinalizeOutcome finOutcome2;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            finOutcome2 = await svc.FinalizeAgainstOperationAsync(tenantId, operationId, "create", default);
            await tx.CommitAsync();
        }

        Assert.Equal(FinalizeOutcome.Finalized, finOutcome2);

        // Slot: booked must still be 1 — no second decrement.
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(1, slot.BookedCapacity); // NOT 2
        }
    }

    // ── Test 7: FinalizeAgainstOperation on already-expired hold → HoldNoLongerValid ───────────

    /// <summary>
    /// FIX 2: FinalizeAgainstOperationAsync on a hold that was expired (released) → HoldNoLongerValid.
    /// Slot booked stays 0 (no double decrement — expiry already freed held).
    /// </summary>
    [Fact]
    public async Task FinalizeAgainstOperationAsync_AlreadyReleasedHold_ReturnsHoldNoLongerValid()
    {
        var tenantId = $"t7-fin-expired-{Guid.NewGuid():N}";
        var (slotId, operationId) = await SeedSlotAndOperationAsync(tenantId, total: 1);

        // Reserve
        string holdId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, operationId, slotId), default);
            holdId = r.Hold!.HoldId;
            await tx.CommitAsync();
        }

        // Release (simulating expiry winning the race)
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            await svc.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, default);
            await tx.CommitAsync();
        }

        // FinalizeAgainstOperationAsync on the already-expired hold: must return HoldNoLongerValid
        FinalizeOutcome finOutcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            finOutcome = await svc.FinalizeAgainstOperationAsync(tenantId, operationId, "create", default);
            await tx.CommitAsync();
        }

        Assert.Equal(FinalizeOutcome.HoldNoLongerValid, finOutcome);

        // Slot: held=0, booked=0 (the finalize did NOT move anything to booked)
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
        }
    }

    // ── Test 7b: FinalizeAgainstOperation never tracked → NotTracked ─────────────────────────────

    /// <summary>
    /// FIX 4: FinalizeAgainstOperationAsync when no hold row exists for (op, step)
    /// returns NotTracked — non-tracked op, caller proceeds to Booked unchanged.
    /// </summary>
    [Fact]
    public async Task FinalizeAgainstOperationAsync_NoHoldRow_ReturnsNotTracked()
    {
        var tenantId = $"t7-fin-untracked-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        FinalizeOutcome finOutcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            finOutcome = await svc.FinalizeAgainstOperationAsync(tenantId, operationId, "create", default);
            await tx.CommitAsync();
        }

        Assert.Equal(FinalizeOutcome.NotTracked, finOutcome);
    }

    // ── Test 8: Overcommit never happens via CHECK constraint ─────────────────

    [Fact]
    public async Task ReserveAsync_AfterSlotFull_CheckConstraintHolds_NoOvercommit()
    {
        var tenantId = $"t7-ovr-{Guid.NewGuid():N}";
        var (slotId, opA) = await SeedSlotAndOperationAsync(tenantId, total: 1);
        string opB;
        await using (var ctx = fixture.CreateContext(tenantId))
            opB = await TestData.SeedOperationAsync(ctx, tenantId);

        // Fill the slot
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            var r = await svc.ReserveAsync(MakeRequest(tenantId, opA, slotId), default);
            Assert.Equal(ReservationStatus.Held, r.Status);
            await tx.CommitAsync();
        }

        // Second reserve must return NoCapacity (conditional WHERE prevents the UPDATE)
        ReservationOutcome outcome;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var svc = MakeService(ctx);
            outcome = await svc.ReserveAsync(MakeRequest(tenantId, opB, slotId), default);
            await tx.CommitAsync();
        }

        Assert.Equal(ReservationStatus.NoCapacity, outcome.Status);

        // Final state: held==1, booked==0 — the CHECK constraint (held+booked <= total=1) is satisfied.
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var slot = await verifyCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(1, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
            // held + booked (1+0=1) <= total (1) — no violation possible
            Assert.True(slot.HeldCapacity + slot.BookedCapacity <= slot.TotalCapacity);
        }
    }
}
