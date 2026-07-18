#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Alba HTTP-level tests for <c>CatalogController</c> (spec 11 §5.2, §7) — the customer-facing
/// published-catalog read path that backs the foreground <c>search_vendor_catalog</c> /
/// <c>get_catalog_item</c> tools (route + params match <c>foreground.py</c> verbatim):
///
/// GET /api/catalog/items?query=&amp;category=&amp;max_results=  → 200 (published-only, tenant-scoped)
/// GET /api/catalog/items/{itemId}                          → 200 | 404
///
/// Asserts published-only visibility (Inv-16), tenant isolation (Inv-13), the optional category
/// filter, the max_results page bound, and the missing-tenant 400 guard.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CatalogReadEndpointTests(AcceptanceApiFixture fixture)
{
    private VendorCatalog Catalog(string tenantId, string catalogId, CatalogStatus status) => new()
    {
        TenantId = tenantId,
        CatalogId = catalogId,
        VersionLabel = catalogId,
        Status = status,
        SourceHash = "sha256-test",
    };

    private VendorCatalogItem Item(
        string tenantId, string catalogId, string itemId, string name, string? category) => new()
        {
            TenantId = tenantId,
            ItemId = itemId,
            CatalogId = catalogId,
            Name = name,
            Description = $"{name} description",
            PriceMinor = 3200,
            Currency = "AUD",
            Category = category,
        };

    private async Task SeedAsync(string tenantId, Action<Cordia.Infrastructure.EntityFramework.ApplicationDbContext> seed)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        seed(ctx);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_ReturnsOnlyPublishedItems_ScopedToTenant()
    {
        var tenantA = $"cat-rd-a-{Guid.NewGuid():N}";
        var tenantB = $"cat-rd-b-{Guid.NewGuid():N}";
        var pubA = $"pub-{Guid.NewGuid():N}";
        var draftA = $"draft-{Guid.NewGuid():N}";
        var pubB = $"pubb-{Guid.NewGuid():N}";

        await SeedAsync(tenantA, ctx =>
        {
            ctx.VendorCatalogs.Add(Catalog(tenantA, pubA, CatalogStatus.Published));
            ctx.VendorCatalogs.Add(Catalog(tenantA, draftA, CatalogStatus.DraftReady));
            ctx.VendorCatalogItems.Add(Item(tenantA, pubA, "a-salmon", "Grilled Salmon", "mains"));
            ctx.VendorCatalogItems.Add(Item(tenantA, pubA, "a-soup", "Tomato Soup", "starters"));
            ctx.VendorCatalogItems.Add(Item(tenantA, draftA, "a-secret", "Secret Draft Dish", "mains"));
        });
        await SeedAsync(tenantB, ctx =>
        {
            ctx.VendorCatalogs.Add(Catalog(tenantB, pubB, CatalogStatus.Published));
            ctx.VendorCatalogItems.Add(Item(tenantB, pubB, "b-item", "B Special", "mains"));
        });

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items?query=");
            x.WithRequestHeader("X-Tenant-Id", tenantA);
            x.StatusCodeShouldBeOk();
        });

        var ids = ItemIds(await result.ReadAsTextAsync());
        Assert.Equal(["a-salmon", "a-soup"], ids.OrderBy(s => s).ToList());
        Assert.DoesNotContain("a-secret", ids); // draft hidden
        Assert.DoesNotContain("b-item", ids);   // other tenant hidden
    }

    [Fact]
    public async Task Search_CategoryFilter_NarrowsToCategory()
    {
        var tenant = $"cat-rd-cf-{Guid.NewGuid():N}";
        var pub = $"pub-{Guid.NewGuid():N}";
        await SeedAsync(tenant, ctx =>
        {
            ctx.VendorCatalogs.Add(Catalog(tenant, pub, CatalogStatus.Published));
            ctx.VendorCatalogItems.Add(Item(tenant, pub, "salmon", "Grilled Salmon", "mains"));
            ctx.VendorCatalogItems.Add(Item(tenant, pub, "soup", "Tomato Soup", "starters"));
        });

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items?query=&category=mains");
            x.WithRequestHeader("X-Tenant-Id", tenant);
            x.StatusCodeShouldBeOk();
        });

        var ids = ItemIds(await result.ReadAsTextAsync());
        Assert.Equal(["salmon"], ids);
    }

    [Fact]
    public async Task Search_MaxResults_BoundsPage()
    {
        var tenant = $"cat-rd-mr-{Guid.NewGuid():N}";
        var pub = $"pub-{Guid.NewGuid():N}";
        await SeedAsync(tenant, ctx =>
        {
            ctx.VendorCatalogs.Add(Catalog(tenant, pub, CatalogStatus.Published));
            ctx.VendorCatalogItems.Add(Item(tenant, pub, "salmon", "Grilled Salmon", "mains"));
            ctx.VendorCatalogItems.Add(Item(tenant, pub, "soup", "Tomato Soup", "starters"));
        });

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items?query=&max_results=1");
            x.WithRequestHeader("X-Tenant-Id", tenant);
            x.StatusCodeShouldBeOk();
        });

        var ids = ItemIds(await result.ReadAsTextAsync());
        Assert.Single(ids);
        Assert.Equal("salmon", ids[0]); // ordered by name → "Grilled Salmon" first
    }

    [Fact]
    public async Task GetItem_Published_Returns200_Unpublished_Returns404()
    {
        var tenant = $"cat-rd-gi-{Guid.NewGuid():N}";
        var pub = $"pub-{Guid.NewGuid():N}";
        var draft = $"draft-{Guid.NewGuid():N}";
        await SeedAsync(tenant, ctx =>
        {
            ctx.VendorCatalogs.Add(Catalog(tenant, pub, CatalogStatus.Published));
            ctx.VendorCatalogs.Add(Catalog(tenant, draft, CatalogStatus.DraftReady));

            // Published item carries the §4.2 rich fields (stored jsonb/text).
            var salmon = Item(tenant, pub, "salmon", "Grilled Salmon", "mains");
            salmon.Modifiers = """[{"modifier_id":"mod_1","name":"Extra cheese"}]""";
            salmon.AvailabilityWindow = """{"days_of_week":["fri"],"start_time":"17:00"}""";
            salmon.ImageRef = "s3://tenant/img/salmon.jpg";
            ctx.VendorCatalogItems.Add(salmon);

            ctx.VendorCatalogItems.Add(Item(tenant, draft, "secret", "Secret Dish", "mains"));
        });

        var ok = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items/salmon");
            x.WithRequestHeader("X-Tenant-Id", tenant);
            x.StatusCodeShouldBeOk();
        });
        var doc = JsonDocument.Parse(await ok.ReadAsTextAsync());
        Assert.Equal("salmon", doc.RootElement.GetProperty("item_id").GetString());
        Assert.Equal("mains", doc.RootElement.GetProperty("category").GetString());

        // §4.2 rich fields: modifiers is a nested array, availability_window a nested object, image_ref a string.
        var modifiers = doc.RootElement.GetProperty("modifiers");
        Assert.Equal(JsonValueKind.Array, modifiers.ValueKind);
        Assert.Equal("mod_1", modifiers[0].GetProperty("modifier_id").GetString());
        var window = doc.RootElement.GetProperty("availability_window");
        Assert.Equal(JsonValueKind.Object, window.ValueKind);
        Assert.Equal("17:00", window.GetProperty("start_time").GetString());
        Assert.Equal("s3://tenant/img/salmon.jpg", doc.RootElement.GetProperty("image_ref").GetString());

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items/secret"); // draft → not published → 404
            x.WithRequestHeader("X-Tenant-Id", tenant);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items/does-not-exist");
            x.WithRequestHeader("X-Tenant-Id", tenant);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task Search_MissingTenantHeader_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items?query=");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    private static List<string> ItemIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("item_id").GetString()!)
            .ToList();
    }
}
