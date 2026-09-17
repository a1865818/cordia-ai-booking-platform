#pragma warning disable xUnit1051
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// <c>tenant_capacity_primitives</c> against real Postgres (#106): the rung round-trips through its
/// text encoding, is tenant-scoped, and the schema refuses the one configuration that would make a
/// first-party hold misreport itself.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class TenantCapacityPrimitiveTableTests(PostgresFixture fixture)
{
    private async Task SeedAsync(string tenantId, string provider, CapacityPrimitive primitive)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.TenantCapacityPrimitives.Add(new TenantCapacityPrimitive
        {
            TenantId = tenantId,
            ProviderName = provider,
            Primitive = primitive,
        });
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData(CapacityPrimitive.NativeHold)]
    [InlineData(CapacityPrimitive.IdempotentPending)]
    [InlineData(CapacityPrimitive.AtomicBook)]
    [InlineData(CapacityPrimitive.CheckThenCreate)]
    public async Task AConfiguredRung_IsWhatTheResolverReturns(CapacityPrimitive primitive)
    {
        var tenantId = $"t-rung-{Guid.NewGuid():N}";
        await SeedAsync(tenantId, "acme_reservations", primitive);

        await using var ctx = fixture.CreateContext(tenantId);
        var resolver = new CapacityPrimitiveResolver(new TenantCapacityPrimitiveRepository(ctx));

        Assert.Equal(primitive, await resolver.ResolveAsync(tenantId, "acme_reservations", null));
    }

    [Fact]
    public async Task TheRung_IsStoredAsItsSnakeCaseName()
    {
        var tenantId = $"t-rung-wire-{Guid.NewGuid():N}";
        await SeedAsync(tenantId, "acme_reservations", CapacityPrimitive.IdempotentPending);

        await using var ctx = fixture.CreateContext(tenantId);
        var stored = await ctx.Database
            .SqlQuery<string>($"SELECT primitive AS \"Value\" FROM public.tenant_capacity_primitives WHERE tenant_id = {tenantId}")
            .SingleAsync();

        Assert.Equal("idempotent_pending", stored);
    }

    /// <summary>Inv-13: another tenant's configuration does not leak into this tenant's resolution.</summary>
    [Fact]
    public async Task AnotherTenantsRow_DoesNotApply()
    {
        var owner = $"t-rung-a-{Guid.NewGuid():N}";
        var other = $"t-rung-b-{Guid.NewGuid():N}";
        await SeedAsync(owner, "acme_reservations", CapacityPrimitive.AtomicBook);

        await using var ctx = fixture.CreateContext(other);
        var resolver = new CapacityPrimitiveResolver(new TenantCapacityPrimitiveRepository(ctx));

        Assert.Equal(CapacityPrimitive.FirstParty, await resolver.ResolveAsync(other, "acme_reservations", null));
    }

    [Fact]
    public async Task TheSchema_RefusesAnyRungButFirstPartyForTheFirstPartyProvider()
    {
        var tenantId = $"t-rung-fp-{Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedAsync(tenantId, CapacityProviders.FirstParty, CapacityPrimitive.AtomicBook));

        Assert.Contains("ck_tenant_capacity_primitives_first_party_rung", ex.InnerException?.Message);
    }
}
