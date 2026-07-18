#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Repositories;

using Cordia.Tests.IntegrationTests.Persistence;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Spec 14 §27.6 — "expired / wrong-scope verification grants fail" and
/// "forged caller ID cannot authorize a mutation", proven against the REAL
/// grant repository SQL (the unit suite mocks it). A presented identity with
/// no valid grant never authorizes (Inv-11; discovery ≠ authorization).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class GrantScopeTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<string> SeedGrantAsync(
        string tenantId,
        VerificationActionEnum action = VerificationActionEnum.Modify,
        string customerRef = "cust-1",
        string? targetBookingId = "bk-1",
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? consumedAt = null)
    {
        var grantId = $"grant-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VerificationGrants.Add(new VerificationGrant
        {
            TenantId = tenantId,
            GrantId = grantId,
            CustomerRef = customerRef,
            AllowedAction = action,
            TargetBookingId = targetBookingId,
            IssuedAt = Now.AddMinutes(-5),
            ExpiresAt = expiresAt ?? Now.AddMinutes(10),
            ConsumedAt = consumedAt,
        });
        await ctx.SaveChangesAsync();
        return grantId;
    }

    [Fact]
    public async Task ExactMatch_IsReturned()
    {
        var tenantId = $"t14-grant-ok-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantId);

        await using var ctx = fixture.CreateContext(tenantId);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantByIdAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Modify, "bk-1", Now);

        Assert.NotNull(grant);
    }

    [Fact]
    public async Task ExpiredGrant_IsRejected()
    {
        var tenantId = $"t14-grant-exp-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantId, expiresAt: Now.AddMinutes(-1));

        await using var ctx = fixture.CreateContext(tenantId);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantByIdAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Modify, "bk-1", Now);

        Assert.Null(grant);
    }

    [Fact]
    public async Task WrongAction_IsRejected()
    {
        var tenantId = $"t14-grant-act-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantId, action: VerificationActionEnum.Modify);

        await using var ctx = fixture.CreateContext(tenantId);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantByIdAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Cancel, "bk-1", Now);

        Assert.Null(grant);
    }

    [Fact]
    public async Task WrongBooking_IsRejected()
    {
        var tenantId = $"t14-grant-bk-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantId, targetBookingId: "bk-1");

        await using var ctx = fixture.CreateContext(tenantId);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantByIdAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Modify, "bk-OTHER", Now);

        Assert.Null(grant);
    }

    [Fact]
    public async Task WrongTenant_IsRejected()
    {
        var tenantA = $"t14-grant-ta-{Guid.NewGuid():N}";
        var tenantB = $"t14-grant-tb-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantA);

        await using var ctx = fixture.CreateContext(tenantB);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantByIdAsync(
            tenantB, grantId, "cust-1", VerificationActionEnum.Modify, "bk-1", Now);

        Assert.Null(grant);
    }

    [Fact]
    public async Task ConsumedGrant_IsRejected_AndCannotBeConsumedTwice()
    {
        var tenantId = $"t14-grant-used-{Guid.NewGuid():N}";
        var grantId = await SeedGrantAsync(tenantId, consumedAt: Now.AddMinutes(-1));

        await using var ctx = fixture.CreateContext(tenantId);
        var repo = new VerificationGrantRepository(ctx);

        Assert.Null(await repo.FindValidGrantByIdAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Modify, "bk-1", Now));
        Assert.Equal(0, await repo.ConsumeAsync(
            tenantId, grantId, "cust-1", VerificationActionEnum.Modify, "bk-1", Now));
    }

    [Fact]
    public async Task ForgedCallerIdentity_WithNoGrant_NeverAuthorizes()
    {
        // Discovery is not authentication (§9.1): knowing a customerRef +
        // booking id (e.g. from caller ID) finds NO valid grant when none was
        // minted by the platform for that identity.
        var tenantId = $"t14-grant-forged-{Guid.NewGuid():N}";
        await SeedGrantAsync(tenantId, customerRef: "cust-real");

        await using var ctx = fixture.CreateContext(tenantId);
        var grant = await new VerificationGrantRepository(ctx).FindValidGrantAsync(
            tenantId, "cust-forged", VerificationActionEnum.Modify, "bk-1", Now);

        Assert.Null(grant);
    }
}
