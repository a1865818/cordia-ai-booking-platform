#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;

namespace Cordia.Tests.IntegrationTests.Menu;

/// <summary>
/// Integration tests for <see cref="VendorCatalogRepository"/> against real PostgreSQL
/// (spec 11 §5.2, §7, Inv-13/Inv-16): only PUBLISHED items are visible, draft/in-review
/// items are hidden, and another tenant's items never leak across the global query filter.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CatalogReadVisibilityTests(PostgresFixture fixture)
{
    private static VendorCatalog Catalog(string tenantId, string catalogId, CatalogStatus status) => new()
    {
        TenantId = tenantId,
        CatalogId = catalogId,
        VersionLabel = catalogId,
        Status = status,
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

    [Fact]
    public async Task SearchAndGet_ReturnOnlyPublishedItems_ScopedToTenant()
    {
        var tenantA = $"t-cat-a-{Guid.NewGuid():N}";
        var tenantB = $"t-cat-b-{Guid.NewGuid():N}";
        var pubCatA = $"pub-{Guid.NewGuid():N}";
        var draftCatA = $"draft-{Guid.NewGuid():N}";
        var pubCatB = $"pubb-{Guid.NewGuid():N}";

        // Tenant A: one published catalog (2 items) + one draft catalog (1 item).
        await using (var ctx = fixture.CreateContext(tenantA))
        {
            ctx.VendorCatalogs.Add(Catalog(tenantA, pubCatA, CatalogStatus.Published));
            ctx.VendorCatalogs.Add(Catalog(tenantA, draftCatA, CatalogStatus.DraftReady));
            ctx.VendorCatalogItems.Add(Item(tenantA, pubCatA, "a-salmon", "Grilled Salmon"));
            ctx.VendorCatalogItems.Add(Item(tenantA, pubCatA, "a-soup", "Tomato Soup"));
            ctx.VendorCatalogItems.Add(Item(tenantA, draftCatA, "a-secret", "Secret Draft Dish"));
            await ctx.SaveChangesAsync();
        }

        // Tenant B: a published catalog with its own item.
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            ctx.VendorCatalogs.Add(Catalog(tenantB, pubCatB, CatalogStatus.Published));
            ctx.VendorCatalogItems.Add(Item(tenantB, pubCatB, "b-item", "B Special"));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantA);
        var repo = new VendorCatalogRepository(readCtx);

        // (1) Search returns only tenant A's published items (not the draft, not tenant B's).
        var all = await repo.SearchPublishedItemsAsync(null, null, 0, 50, CancellationToken.None);
        var ids = all.Select(i => i.ItemId).ToList();
        Assert.Equal(["a-salmon", "a-soup"], ids.OrderBy(x => x).ToList());
        Assert.DoesNotContain("a-secret", ids);
        Assert.DoesNotContain("b-item", ids);

        // (2) Query filter (case-insensitive) narrows to matching items.
        var salmon = await repo.SearchPublishedItemsAsync("salmon", null, 0, 50, CancellationToken.None);
        Assert.Single(salmon);
        Assert.Equal("a-salmon", salmon[0].ItemId);

        // (3) Paging: ordered by name, take=1 → "Grilled Salmon" first.
        var firstPage = await repo.SearchPublishedItemsAsync(null, null, 0, 1, CancellationToken.None);
        Assert.Single(firstPage);
        Assert.Equal("a-salmon", firstPage[0].ItemId);

        // (4) GetPublishedItem: published → found; draft → null; cross-tenant → null.
        Assert.NotNull(await repo.GetPublishedItemAsync("a-salmon", CancellationToken.None));
        Assert.Null(await repo.GetPublishedItemAsync("a-secret", CancellationToken.None));
        Assert.Null(await repo.GetPublishedItemAsync("b-item", CancellationToken.None));
    }
}
