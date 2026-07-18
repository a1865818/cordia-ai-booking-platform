#pragma warning disable xUnit1051
using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Menu.PublishCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Menu;

/// <summary>
/// Integration tests for publish immutability + supersede (spec 11 §5.2) against real PostgreSQL:
/// publishing v2 supersedes the prior live v1, and the published read path then returns ONLY v2's
/// items. Drives the real <see cref="PublishCatalogCommandHandler"/> over a tenant-scoped context.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PublishImmutabilityTests(PostgresFixture fixture)
{
    private static VendorCatalog Approved(string tenantId, string catalogId, string label) => new()
    {
        TenantId = tenantId,
        CatalogId = catalogId,
        VersionLabel = label,
        Status = CatalogStatus.Approved,
        SourceHash = "sha256-test",
    };

    private static VendorCatalogItem Item(string tenantId, string catalogId, string itemId, string name) => new()
    {
        TenantId = tenantId,
        ItemId = itemId,
        CatalogId = catalogId,
        Name = name,
        Description = $"{name} description",
        PriceMinor = 3200,
        Currency = "AUD",
    };

    private async Task PublishAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new PublishCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditWriter(new AuditEventRepository(ctx)), new UnitOfWork(ctx));
        await handler.Handle(new PublishCatalogCommand(tenantId, catalogId, "owner", "rc_test", "owner"), CancellationToken.None);
    }

    [Fact]
    public async Task PublishingV2_SupersedesV1_AndReadReturnsOnlyV2()
    {
        var tenantId = $"t-pub-{Guid.NewGuid():N}";
        var v1 = $"cat-v1-{Guid.NewGuid():N}";
        var v2 = $"cat-v2-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VendorCatalogs.Add(Approved(tenantId, v1, "menu_v1"));
            ctx.VendorCatalogItems.Add(Item(tenantId, v1, $"{v1}:old", "Old Dish"));
            ctx.VendorCatalogs.Add(Approved(tenantId, v2, "menu_v2"));
            ctx.VendorCatalogItems.Add(Item(tenantId, v2, $"{v2}:new", "New Dish"));
            await ctx.SaveChangesAsync();
        }

        // Publish v1 (no prior) → published.
        await PublishAsync(tenantId, v1);

        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var only = await new VendorCatalogRepository(ctx1).SearchPublishedItemsAsync(null, null, 0, 50, CancellationToken.None);
            Assert.Equal(["Old Dish"], only.Select(i => i.Name).ToList());
        }

        // Publish v2 → supersedes v1.
        await PublishAsync(tenantId, v2);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var v1Row = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == v1);
        var v2Row = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == v2);
        Assert.Equal(CatalogStatus.Superseded, v1Row.Status);
        Assert.Equal(CatalogStatus.Published, v2Row.Status);

        // The published read path now returns only v2's items (v1 is immutable + superseded).
        var items = await new VendorCatalogRepository(assertCtx).SearchPublishedItemsAsync(null, null, 0, 50, CancellationToken.None);
        Assert.Equal(["New Dish"], items.Select(i => i.Name).ToList());

        // Supersede audit recorded against the prior version.
        var supersedeAudit = await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EventType == "catalog.superseded" && e.SubjectRef == v1);
        Assert.NotNull(supersedeAudit);
    }
}
