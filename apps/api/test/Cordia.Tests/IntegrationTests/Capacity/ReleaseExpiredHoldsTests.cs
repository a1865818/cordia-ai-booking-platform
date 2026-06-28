#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Behaviours;
using Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// Integration tests for <see cref="ReleaseExpiredHoldsCommandHandler"/> (spec 07 T6).
///
/// Tests drive the handler through <see cref="InboxDeduplicationBehavior{TRequest,TResponse}"/>
/// with real repos over <see cref="PostgresFixture"/> — the same pattern as
/// <c>InboxDeduplicationBehaviorTests</c> — so inbox deduplication (Inv-5) is exercised alongside
/// the release logic.
///
/// Each test uses a unique tenantId for row isolation; assertions are scoped to own ids only
/// (shared test DB; see memory "bg-worker-test-flakiness").
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ReleaseExpiredHoldsTests(PostgresFixture fixture)
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the handler with real repos sharing <paramref name="ctx"/>.
    /// </summary>
    private static ReleaseExpiredHoldsCommandHandler BuildHandler(ApplicationDbContext ctx)
    {
        var holdRepo = new CapacityHoldRepository(ctx);
        var capacityReservation = new FirstPartyCapacityReservation(ctx, holdRepo);
        var auditRepo = new AuditEventRepository(ctx);
        return new ReleaseExpiredHoldsCommandHandler(capacityReservation, auditRepo);
    }

    /// <summary>
    /// Drives the handler through <see cref="InboxDeduplicationBehavior{TRequest,TResponse}"/>
    /// with real inbox/UoW repos over <paramref name="ctx"/>.
    /// </summary>
    private static async Task<Unit> DriveWithInboxAsync(
        ApplicationDbContext ctx,
        ReleaseExpiredHoldsCommand cmd,
        CancellationToken ct = default)
    {
        var uow = new UnitOfWork(ctx);
        var inboxRepo = new InboxRepository(ctx);
        var handler = BuildHandler(ctx);

        RequestHandlerDelegate<Unit> next = ct2 => handler.Handle(cmd, ct2);

        var behavior = new InboxDeduplicationBehavior<ReleaseExpiredHoldsCommand, Unit>(uow, inboxRepo);
        return await behavior.Handle(cmd, next, ct);
    }

    private static CapacitySlot MakeSlot(string tenantId, string slotId, int total, int held = 0)
        => new()
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = $"res-{slotId}",
            StartsAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalCapacity = total,
            HeldCapacity = held,
            BookedCapacity = 0,
            Version = 0,
        };

    private static ReserveRequest MakeReserveRequest(
        string tenantId,
        string operationId,
        string slotId,
        int capacity = 1)
        => new(
            tenantId,
            operationId,
            "create",
            slotId,
            capacity,
            DateTimeOffset.UtcNow.AddMinutes(30),
            "first_party");

    /// <summary>
    /// Seeds a slot (total=<paramref name="total"/>) and one active hold against it.
    /// Returns (slotId, operationId, holdId).
    /// </summary>
    private async Task<(string slotId, string operationId, string holdId)> SeedSlotAndActiveHoldAsync(
        string tenantId,
        int total = 1)
    {
        var slotId = $"slot-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        // Insert slot.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.CapacitySlots.Add(MakeSlot(tenantId, slotId, total, held: 0));
            await ctx.SaveChangesAsync();
        }

        // Reserve to create an active hold.
        string holdId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            var holdRepo = new CapacityHoldRepository(ctx);
            var svc = new FirstPartyCapacityReservation(ctx, holdRepo);
            var outcome = await svc.ReserveAsync(MakeReserveRequest(tenantId, operationId, slotId), default);
            Assert.Equal(ReservationStatus.Held, outcome.Status);
            holdId = outcome.Hold!.HoldId;
            await tx.CommitAsync();
        }

        return (slotId, operationId, holdId);
    }

    // ── Test 1: Scheduled expiry (HoldId present) releases the hold and frees the slot ──────

    /// <summary>
    /// Simulates the <c>booking.scheduled.hold_expiry.v1</c> path:
    /// <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is present.
    /// Asserts: hold status == Expired, slot held_capacity == 0.
    /// </summary>
    [Fact]
    public async Task ScheduledExpiry_HoldIdPresent_ReleasesHold_FreesSlot()
    {
        var tenantId = $"t7-t6-sched-{Guid.NewGuid():N}";
        var (slotId, operationId, holdId) = await SeedSlotAndActiveHoldAsync(tenantId);
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, holdId, messageId);
            await DriveWithInboxAsync(ctx, cmd);
        }

        // Verify: hold is Expired.
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Expired, hold.Status);
            Assert.Equal(ReleaseReason.Expired, hold.ReleaseReason);

            // Slot held_capacity freed.
            var slot = await verifyCtx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
        }
    }

    // ── Test 2: Idempotent duplicate delivery — inbox dedup suppresses second call ────────────

    /// <summary>
    /// Same <see cref="ReleaseExpiredHoldsCommand.MessageId"/> delivered twice.
    /// Asserts:
    ///   - First delivery: hold released, slot freed.
    ///   - Second delivery: suppressed by inbox dedup (hold remains Expired, slot remains 0).
    ///   - Slot held_capacity stays 0, not negative.
    /// </summary>
    [Fact]
    public async Task DuplicateDelivery_SameMessageId_IsIdempotentNoOp()
    {
        var tenantId = $"t7-t6-dup-{Guid.NewGuid():N}";
        var (slotId, operationId, holdId) = await SeedSlotAndActiveHoldAsync(tenantId);
        var messageId = $"msg-{Guid.NewGuid():N}";

        // First delivery — should release the hold.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, holdId, messageId);
            await DriveWithInboxAsync(ctx, cmd);
        }

        // Verify first release.
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Expired, hold.Status);

            var slot = await verifyCtx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
        }

        // Second delivery — SAME messageId → inbox dedup suppresses; handler not invoked.
        await using (var ctx2 = fixture.CreateContext(tenantId))
        {
            var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, holdId, messageId);
            await DriveWithInboxAsync(ctx2, cmd);
        }

        // Verify: hold still Expired, slot still 0 (no double-decrement).
        await using (var verifyCtx2 = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx2.CapacityHolds.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Expired, hold.Status); // unchanged

            var slot = await verifyCtx2.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity); // must NOT go negative

            // Exactly one consumer_inbox row (dedup row is unique).
            var inboxCount = await verifyCtx2.ConsumerInbox.AsNoTracking()
                .CountAsync(e => e.ConsumerName == "capacity.release" && e.MessageId == messageId);
            Assert.Equal(1, inboxCount);
        }
    }

    // ── Test 3: capacity.hold_release.requested.v1 path (HoldId null) ─────────────────────────

    /// <summary>
    /// Simulates the deposit-expiry path (<c>capacity.hold_release.requested.v1</c>):
    /// <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is null — releases all active holds for the op.
    /// Asserts: hold status == Released (Compensated/ProviderReleased → Released; but Expired → Expired;
    /// in this path reason=Expired), slot held_capacity == 0.
    /// </summary>
    [Fact]
    public async Task DepositExpiryPath_HoldIdNull_ReleasesHoldByOperation_FreesSlot()
    {
        var tenantId = $"t7-t6-depexp-{Guid.NewGuid():N}";
        var (slotId, operationId, holdId) = await SeedSlotAndActiveHoldAsync(tenantId);
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // HoldId = null → ReleaseByOperationAsync path.
            var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, null, messageId);
            await DriveWithInboxAsync(ctx, cmd);
        }

        // Verify: hold is Expired (reason=Expired → HoldStatus.Expired per mapping in FirstPartyCapacityReservation).
        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var hold = await verifyCtx.CapacityHolds.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Expired, hold.Status);
            Assert.Equal(ReleaseReason.Expired, hold.ReleaseReason);

            // Slot freed.
            var slot = await verifyCtx.CapacitySlots.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(0, slot.BookedCapacity);
        }
    }
}
