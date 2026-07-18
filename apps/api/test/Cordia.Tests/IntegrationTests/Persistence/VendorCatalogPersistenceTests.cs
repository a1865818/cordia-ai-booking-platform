#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class VendorCatalogPersistenceTests(PostgresFixture fixture)
{
    // (1) Insert catalog (status Uploaded) + item (FK), read back; status enum + jsonb round-trip.
    [Fact]
    public async Task Insert_CatalogAndItem_RoundTripsEnumAndJsonb()
    {
        var tenantId = $"t5-vc-{Guid.NewGuid():N}";
        var catalogId = $"cat-{Guid.NewGuid():N}";
        var itemId = $"item-{Guid.NewGuid():N}";

        var catalog = new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = "menu-2026-v1",
            Status = CatalogStatus.Uploaded,
            SourceHash = "sha256-abc123",
        };

        var item = new VendorCatalogItem
        {
            TenantId = tenantId,
            ItemId = itemId,
            CatalogId = catalogId,
            Name = "Grilled Salmon",
            Description = "Atlantic salmon, lemon butter",
            PriceMinor = 3200,
            Currency = "AUD",
            MenuMetadata = """{"category":"mains","availability":"lunch,dinner"}""",
            AllergenInfo = """{"contains":["fish","dairy"]}""",
        };

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VendorCatalogs.Add(catalog);
            ctx.VendorCatalogItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);

        var loadedCatalog = await readCtx.VendorCatalogs.AsNoTracking()
            .SingleAsync(c => c.CatalogId == catalogId);

        Assert.Equal(CatalogStatus.Uploaded, loadedCatalog.Status);
        Assert.Equal("menu-2026-v1", loadedCatalog.VersionLabel);

        var loadedItem = await readCtx.VendorCatalogItems.AsNoTracking()
            .SingleAsync(i => i.ItemId == itemId);

        Assert.Equal(catalogId, loadedItem.CatalogId);
        Assert.Equal(3200, loadedItem.PriceMinor);

        // jsonb round-trip: PostgreSQL may reorder keys/add spaces; compare via parsed JSON.
        var menuDoc = JsonDocument.Parse(loadedItem.MenuMetadata!);
        Assert.Equal("mains", menuDoc.RootElement.GetProperty("category").GetString());
        Assert.Equal("lunch,dinner", menuDoc.RootElement.GetProperty("availability").GetString());

        var allergenDoc = JsonDocument.Parse(loadedItem.AllergenInfo!);
        var contains = allergenDoc.RootElement.GetProperty("contains").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("fish", contains);
        Assert.Contains("dairy", contains);
    }

    // (2) Version concurrency token defaults to 0 on insert and round-trips (§22.1).
    [Fact]
    public async Task Insert_Catalog_VersionTokenDefaultsToZero()
    {
        var tenantId = $"t5-vcv-{Guid.NewGuid():N}";
        var catalogId = $"cat-{Guid.NewGuid():N}";

        var catalog = new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = "menu-2026-v1",
            Status = CatalogStatus.Uploaded,
            SourceHash = "sha256-abc123",
        };

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VendorCatalogs.Add(catalog);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);

        var loaded = await readCtx.VendorCatalogs.AsNoTracking()
            .SingleAsync(c => c.CatalogId == catalogId);

        Assert.Equal(0L, loaded.Version);
    }
}
