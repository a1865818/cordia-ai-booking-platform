#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// The draft-items read that unblocks the owner edit action (docs §12 gap 4 / §13 B1).
///
/// `POST /api/menu/catalogs/{id}/edit` keys on the namespaced `{catalogId}:{rawId}` item id, and
/// `VendorCatalogItems.ItemId` already stores exactly that — so no id scheme had to be designed.
/// The obstacle was that every existing read is published-scoped
/// (`SearchPublishedItemsAsync` / `GetPublishedItemsAsync` both filter `Status == Published`),
/// which by construction excludes a catalog in `in_review` — the only status in which editing is
/// legal. Hence a draft-scoped, owner-gated read.
///
/// Returning the catalog's own status alongside the items also relieves §12 gap 3: the console
/// can finally observe a catalog's real status rather than tracking only what its own writes
/// confirmed.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class MenuDraftItemsTests(AcceptanceApiFixture fixture)
{
    private async Task<string> SeedCatalogWithItemsAsync(
        string tenantId, CatalogStatus status, int itemCount = 2)
    {
        var catalogId = TestIds.New("cat");
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = catalogId,
            Status = status,
            SourceHash = "sha256-test",
        });
        for (var i = 0; i < itemCount; i++)
        {
            ctx.VendorCatalogItems.Add(new VendorCatalogItem
            {
                TenantId = tenantId,
                // The namespaced form the edit action keys on (M5 symmetry).
                ItemId = $"{catalogId}:itm{i}",
                CatalogId = catalogId,
                Name = $"Item {i}",
                Description = $"Description {i}",
                PriceMinor = 4500 + i,
                Currency = "AUD",
            });
        }
        await ctx.SaveChangesAsync();
        return catalogId;
    }

    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "reviewing the draft before publishing");
    }

    [Fact]
    public async Task ReturnsDraftItemsWithTheNamespacedIdsTheEditActionRequires()
    {
        var tenantId = TestIds.New("menu-draft");
        var catalogId = await SeedCatalogWithItemsAsync(tenantId, CatalogStatus.InReview);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/menu/catalogs/{catalogId}/items");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        // Relieves §12 gap 3 — a real status, not one inferred from this session's own writes.
        Assert.Equal("in_review", root.GetProperty("status").GetString());
        Assert.Equal(catalogId, root.GetProperty("catalog_id").GetString());

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        // The whole point: the id must be the namespaced form POST .../edit keys on. A raw
        // `itm0` here would be silently unusable and the edit would 404.
        var itemId = items[0].GetProperty("item_id").GetString();
        Assert.StartsWith($"{catalogId}:", itemId);
        Assert.Equal("Item 0", items[0].GetProperty("name").GetString());
        Assert.Equal(4500, items[0].GetProperty("price_minor").GetInt64());
    }

    [Fact]
    public async Task ReadsADraftCatalogThatThePublishedScopedReadsCannotSee()
    {
        // draft_ready is the status the published-scoped reads exclude by construction.
        var tenantId = TestIds.New("menu-draftready");
        var catalogId = await SeedCatalogWithItemsAsync(tenantId, CatalogStatus.DraftReady);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/menu/catalogs/{catalogId}/items");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("draft_ready", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnEmptyCatalogIsARealAnswerRatherThanA404()
    {
        var tenantId = TestIds.New("menu-empty");
        var catalogId = await SeedCatalogWithItemsAsync(tenantId, CatalogStatus.InReview, itemCount: 0);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/menu/catalogs/{catalogId}/items");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        // "extraction produced nothing" and "no such catalog" are different facts.
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task UnknownCatalogIs404()
    {
        var tenantId = TestIds.New("menu-unknown");

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/menu/catalogs/cat-does-not-exist/items");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task NonOwnerIsRefused()
    {
        var tenantId = TestIds.New("menu-role");
        var catalogId = await SeedCatalogWithItemsAsync(tenantId, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/menu/catalogs/{catalogId}/items");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            // The draft carries un-reviewed, machine-extracted content; the same role gate as
            // every other step of the D4 lifecycle applies.
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "curious");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task MissingTenantIs400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/menu/catalogs/cat-1/items");
            x.WithRequestHeader("X-Actor", "owner@example.com");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // -------------------------------------------------------------------------
    // Inv-13 — a draft is un-reviewed extracted content; it must not cross tenants.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DoesNotReturnAnotherTenantsDraft()
    {
        var tenantA = TestIds.New("menu-xta");
        var tenantB = TestIds.New("menu-xtb");
        var catalogId = await SeedCatalogWithItemsAsync(tenantA, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/menu/catalogs/{catalogId}/items");
            Owner(x, tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }
}
