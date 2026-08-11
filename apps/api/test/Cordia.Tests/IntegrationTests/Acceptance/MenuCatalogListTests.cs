#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Catalog discovery (docs §12 gap 3).
///
/// Before this, the console tracked only catalogs it had started in the current browser session,
/// so an ingestion begun on another machine simply did not exist as far as the operator was
/// concerned — and a catalog stuck mid-extraction could not be told from one never registered.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class MenuCatalogListTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "checking ingestion progress");
    }

    private async Task SeedAsync(string tenantId, string catalogId, CatalogStatus status, DateTimeOffset createdAt)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = catalogId,
            Status = status,
            SourceHash = "sha256-test",
            CreatedAt = createdAt,
            // `age_seconds` reports time at the CURRENT STATUS, not since upload, so this is the
            // field the age assertions are actually about. Left unset it takes the column's
            // `now()` default and every seeded catalog reads as brand new.
            StatusChangedAt = createdAt,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<JsonElement> ListAsync(string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/menu/catalogs");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });
        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ListsCatalogsStartedOutsideThisSession()
    {
        var tenantId = TestIds.New("cat-list");
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(tenantId, TestIds.New("cat"), CatalogStatus.Extracting, now.AddMinutes(-30));
        await SeedAsync(tenantId, TestIds.New("cat"), CatalogStatus.Published, now.AddMinutes(-5));

        var root = await ListAsync(tenantId);
        var catalogs = root.GetProperty("catalogs");

        Assert.Equal(2, catalogs.GetArrayLength());
        // Newest first, so the thing an operator most likely just did is at the top.
        Assert.Equal("published", catalogs[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReportsHowLongACatalogHasSatWhereItIs()
    {
        // The honest substitute for `extract_failed`, which D4 does not ratify: age is a real
        // signal, not a verdict. A catalog extracting for half an hour is visibly not fresh —
        // the console can say so without claiming the platform has declared a failure.
        var tenantId = TestIds.New("cat-age");
        await SeedAsync(tenantId, TestIds.New("cat"), CatalogStatus.Extracting, DateTimeOffset.UtcNow.AddMinutes(-30));

        var root = await ListAsync(tenantId);
        var age = root.GetProperty("catalogs")[0].GetProperty("age_seconds").GetInt64();

        Assert.InRange(age, 1500, 2400);
    }

    [Fact]
    public async Task AnEmptyListIsARealAnswer()
    {
        var root = await ListAsync(TestIds.New("cat-empty"));

        // "this venue has registered no catalogs" and "the read failed" are different facts.
        Assert.Equal(0, root.GetProperty("catalogs").GetArrayLength());
    }

    [Fact]
    public async Task NonOwnerIsRefused()
    {
        var tenantId = TestIds.New("cat-role");
        await SeedAsync(tenantId, TestIds.New("cat"), CatalogStatus.InReview, DateTimeOffset.UtcNow);

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/menu/catalogs");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
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
            x.Get.Url("/api/menu/catalogs");
            x.WithRequestHeader("X-Actor", "owner@example.com");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task DoesNotListAnotherTenantsCatalogs()
    {
        var tenantA = TestIds.New("cat-xta");
        var tenantB = TestIds.New("cat-xtb");
        await SeedAsync(tenantA, TestIds.New("cat"), CatalogStatus.InReview, DateTimeOffset.UtcNow);

        var root = await ListAsync(tenantB);

        Assert.Equal(0, root.GetProperty("catalogs").GetArrayLength());
    }
}
