#pragma warning disable xUnit1051
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// G6: published catalog search and detail emit <c>duration_minutes</c> when set on the item.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CatalogDurationMinutesTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task SearchAndDetail_IncludeDurationMinutesWhenSetOnPublishedItem()
    {
        var tenantId = TestIds.New("cat-dur");
        var catalogId = $"pub-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.VendorCatalogs.Add(new VendorCatalog
            {
                TenantId = tenantId,
                CatalogId = catalogId,
                VersionLabel = catalogId,
                Status = CatalogStatus.Published,
                SourceHash = "sha256-test",
            });
            ctx.VendorCatalogItems.Add(new VendorCatalogItem
            {
                TenantId = tenantId,
                ItemId = "gel-manicure",
                CatalogId = catalogId,
                Name = "Gel manicure",
                Description = "Classic gel",
                PriceMinor = 5500,
                Currency = "AUD",
                Category = "nails",
                DurationMinutes = 45,
            });
            await ctx.SaveChangesAsync();
        }

        var search = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items?query=");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using (var searchDoc = JsonDocument.Parse(await search.ReadAsTextAsync()))
        {
            Assert.Equal(1, searchDoc.RootElement.GetArrayLength());
            Assert.Equal(45, searchDoc.RootElement[0].GetProperty("duration_minutes").GetInt32());
            Assert.Equal("gel-manicure", searchDoc.RootElement[0].GetProperty("item_id").GetString());
        }

        var detail = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/catalog/items/gel-manicure");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var detailDoc = JsonDocument.Parse(await detail.ReadAsTextAsync());
        Assert.Equal(45, detailDoc.RootElement.GetProperty("duration_minutes").GetInt32());
    }
}
