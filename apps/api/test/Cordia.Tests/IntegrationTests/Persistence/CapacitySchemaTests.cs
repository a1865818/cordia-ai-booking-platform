#pragma warning disable xUnit1051
using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CapacitySchemaTests(PostgresFixture fixture)
{
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

    private static CapacityHold MakeHold(string tenantId, string holdId, string operationId, string slotId)
        => new()
        {
            TenantId = tenantId,
            HoldId = holdId,
            OperationId = operationId,
            WorkflowStep = "create",
            SlotId = slotId,
            ReservedCapacity = 1,
            Status = HoldStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Version = 0,
        };

    // (1) Happy path: insert a valid slot + an active hold, assert they persist.
    [Fact]
    public async Task Insert_ValidSlotAndHold_Succeeds()
    {
        var tenantId = $"t7-cap-happy-{Guid.NewGuid():N}";
        var slotId = $"slot-{Guid.NewGuid():N}";
        var holdId = $"hold-{Guid.NewGuid():N}";

        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacitySlots.Add(MakeSlot(tenantId, slotId, total: 2));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacityHolds.Add(MakeHold(tenantId, holdId, operationId, slotId));
            await ctx.SaveChangesAsync();
        }

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var slot = await verifyCtx.CapacitySlots.AsNoTracking().SingleAsync(s => s.SlotId == slotId);
        var hold = await verifyCtx.CapacityHolds.AsNoTracking().SingleAsync(h => h.HoldId == holdId);

        Assert.Equal(2, slot.TotalCapacity);
        Assert.Equal(HoldStatus.Active, hold.Status);
    }

    // (2) ck_capacity_slots_no_overcommit: held+booked > total must throw DbUpdateException.
    [Fact]
    public async Task Insert_SlotViolatingOvercommitCheck_ThrowsDbUpdateException()
    {
        var tenantId = $"t7-cap-over-{Guid.NewGuid():N}";
        var slotId = $"slot-{Guid.NewGuid():N}";

        // held=3, booked=0, total=2 → held+booked (3) > total (2) — overcommit
        var badSlot = MakeSlot(tenantId, slotId, total: 2, held: 3, booked: 0);

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacitySlots.Add(badSlot);
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // (3) uq_active_hold_per_op_step: two active holds for the same (operation, workflow_step) must fail.
    [Fact]
    public async Task Insert_TwoActiveHoldsSameOpStep_ThrowsDbUpdateException()
    {
        var tenantId = $"t7-cap-uq-{Guid.NewGuid():N}";
        var slotId = $"slot-{Guid.NewGuid():N}";

        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacitySlots.Add(MakeSlot(tenantId, slotId, total: 5));
            await ctx.SaveChangesAsync();
        }

        var hold1 = MakeHold(tenantId, $"hold-{Guid.NewGuid():N}", operationId, slotId);
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacityHolds.Add(hold1);
            await ctx.SaveChangesAsync();
        }

        var hold2 = MakeHold(tenantId, $"hold-{Guid.NewGuid():N}", operationId, slotId);
        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.CapacityHolds.Add(hold2);
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
    }

    // (4) ix_capacity_holds_expiry partial index exists.
    [Fact]
    public async Task PartialIndex_IxCapacityHoldsExpiry_Exists()
    {
        await using var ctx = fixture.CreateContext("t7-cap-idx");
        var exists = await ctx.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS value FROM pg_indexes WHERE indexname = 'ix_capacity_holds_expiry'")
            .AnyAsync();
        Assert.True(exists);
    }

    // (5) uq_active_hold_per_op_step partial index exists.
    [Fact]
    public async Task PartialIndex_UqActiveHoldPerOpStep_Exists()
    {
        await using var ctx = fixture.CreateContext("t7-cap-idx2");
        var exists = await ctx.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS value FROM pg_indexes WHERE indexname = 'uq_active_hold_per_op_step'")
            .AnyAsync();
        Assert.True(exists);
    }
}
