#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// HTTP round-trip for GET/PUT /api/tenant-profile (docs/backend-gaps.md G4 / G10).
/// Display-name routes stay covered by <see cref="TenantDisplayNameTests"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class TenantProfileTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    [Fact]
    public async Task PutAndGet_RoundTripBusinessTypeAndWaitlistSwitch()
    {
        var tenantId = TestIds.New("tp-set");

        var put = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { business_type = "salon", waitlist_auto_book = true })
                .ToUrl("/api/tenant-profile");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using (var putDoc = JsonDocument.Parse(await put.ReadAsTextAsync()))
        {
            Assert.Equal(tenantId, putDoc.RootElement.GetProperty("tenant_id").GetString());
            Assert.Equal("salon", putDoc.RootElement.GetProperty("business_type").GetString());
            Assert.True(putDoc.RootElement.GetProperty("waitlist_auto_book").GetBoolean());
            Assert.Equal(string.Empty, putDoc.RootElement.GetProperty("display_name").GetString());
        }

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var getDoc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.Equal("salon", getDoc.RootElement.GetProperty("business_type").GetString());
        Assert.True(getDoc.RootElement.GetProperty("waitlist_auto_book").GetBoolean());
    }

    [Fact]
    public async Task Get_MissingProfile_ReturnsNullBusinessTypeAndWaitlistOff()
    {
        var tenantId = TestIds.New("tp-miss");

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("business_type").ValueKind);
        Assert.False(doc.RootElement.GetProperty("waitlist_auto_book").GetBoolean());
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task NonOwnerCannotPut()
    {
        var tenantId = TestIds.New("tp-role");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { business_type = "restaurant" }).ToUrl("/api/tenant-profile");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task Put_JoinsDisplayNameSetOnTheSiblingRoute()
    {
        var tenantId = TestIds.New("tp-dn");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { display_name = "Serenity Spa" }).ToUrl("/api/tenant-profile/display-name");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.Equal("Serenity Spa", doc.RootElement.GetProperty("display_name").GetString());
    }
}
