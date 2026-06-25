#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class VerificationGrantPersistenceTests(PostgresFixture fixture)
{
    private static VerificationGrant MakeGrant(
        string tenantId,
        string customerRef,
        VerificationActionEnum action,
        VerificationLevel level,
        DateTimeOffset expiresAt,
        string? targetBookingId = null,
        DateTimeOffset? consumedAt = null)
        => new()
        {
            TenantId = tenantId,
            GrantId = $"grant-{Guid.NewGuid():N}",
            CustomerRef = customerRef,
            AllowedAction = action,
            VerificationLevel = level,
            ExpiresAt = expiresAt,
            TargetBookingId = targetBookingId,
            ConsumedAt = consumedAt,
        };

    // (1) FindValidGrantAsync returns a valid grant.
    [Fact]
    public async Task FindValidGrantAsync_ReturnsValidGrant()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1),
            targetBookingId: "bk-1");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Modify, "bk-1", now);

        Assert.NotNull(found);
        Assert.Equal(VerificationActionEnum.Modify, found.AllowedAction);
        Assert.Equal(VerificationLevel.IdentityVerified, found.VerificationLevel);
    }

    // (2) Expired grant → FindValid returns null.
    [Fact]
    public async Task FindValidGrantAsync_ExpiredGrant_ReturnsNull()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(-1)); // already expired

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Modify, null, now);

        Assert.Null(found);
    }

    // (3) Consumed grant → FindValid returns null.
    [Fact]
    public async Task FindValidGrantAsync_ConsumedGrant_ReturnsNull()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1),
            consumedAt: now.AddMinutes(-5)); // already consumed

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Modify, null, now);

        Assert.Null(found);
    }

    // (4) Wrong action (Cancel) → FindValid returns null.
    [Fact]
    public async Task FindValidGrantAsync_WrongAction_ReturnsNull()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1));

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Cancel, null, now);

        Assert.Null(found);
    }

    // (4b) Wrong customer → FindValid returns null (grant for cust-1, queried as cust-2).
    [Fact]
    public async Task FindValidGrantAsync_WrongCustomer_ReturnsNull()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1));

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-2", VerificationActionEnum.Modify, null, now);

        Assert.Null(found);
    }

    // (4c) Booking-scoped grant must not match a different booking (bk-1 grant, queried for bk-2).
    [Fact]
    public async Task FindValidGrantAsync_WrongBooking_ReturnsNull()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1),
            targetBookingId: "bk-1");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Modify, "bk-2", now);

        Assert.Null(found);
    }

    // (4d) Customer-wide grant (TargetBookingId null) matches a specific-booking request (§5.5 "null => customer-wide").
    [Fact]
    public async Task FindValidGrantAsync_CustomerWideGrant_MatchesSpecificBooking()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-1", VerificationActionEnum.Modify,
            VerificationLevel.IdentityVerified, now.AddHours(1),
            targetBookingId: null); // customer-wide

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(readCtx);
        var found = await repo.FindValidGrantAsync(tenantId, "cust-1", VerificationActionEnum.Modify, "bk-9", now);

        Assert.NotNull(found);
    }

    // (5) ConsumeAsync sets consumed_at (rows=1); second ConsumeAsync → rows=0 (single-use).
    //     Assert AllowedAction + VerificationLevel round-trip.
    [Fact]
    public async Task ConsumeAsync_SingleUse_SecondCallReturnsZero()
    {
        var tenantId = $"t4-vg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var grant = MakeGrant(
            tenantId, "cust-2", VerificationActionEnum.Cancel,
            VerificationLevel.ChannelVerified, now.AddHours(1));

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VerificationGrants.Add(grant);
            await ctx.SaveChangesAsync();
        }

        // First consume → rows = 1
        await using var ctx1 = fixture.CreateContext(tenantId);
        var repo1 = new VerificationGrantRepository(ctx1);
        var rows1 = await repo1.ConsumeAsync(tenantId, grant.GrantId, now);
        Assert.Equal(1, rows1);

        // Second consume → rows = 0
        await using var ctx2 = fixture.CreateContext(tenantId);
        var repo2 = new VerificationGrantRepository(ctx2);
        var rows2 = await repo2.ConsumeAsync(tenantId, grant.GrantId, now.AddSeconds(1));
        Assert.Equal(0, rows2);

        // Verify AllowedAction + VerificationLevel round-trip from DB
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.VerificationGrants.AsNoTracking()
            .SingleAsync(g => g.GrantId == grant.GrantId);

        Assert.Equal(VerificationActionEnum.Cancel, reloaded.AllowedAction);
        Assert.Equal(VerificationLevel.ChannelVerified, reloaded.VerificationLevel);
        Assert.NotNull(reloaded.ConsumedAt);
    }
}
