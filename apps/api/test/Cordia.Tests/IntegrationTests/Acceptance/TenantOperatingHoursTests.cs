#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// HTTP tests for GET/PUT /api/tenant-profile/hours (docs/backend-gaps.md G5).
/// Empty stored hours return Mon–Sat 09:00–18:00 on, Sunday off (seed default).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class TenantOperatingHoursTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    private static object Day(string d, string open, string close, bool on)
        => new { d, open, close, on };

    private static object[] SevenDays() =>
    [
        Day("Monday", "09:00", "18:00", true),
        Day("Tuesday", "09:00", "18:00", true),
        Day("Wednesday", "10:00", "16:00", true),
        Day("Thursday", "09:00", "18:00", true),
        Day("Friday", "09:00", "20:00", true),
        Day("Saturday", "09:00", "14:00", true),
        Day("Sunday", "09:00", "18:00", false),
    ];

    [Fact]
    public async Task Get_WithNothingStored_ReturnsMondayToSaturdayOpenSundayOff()
    {
        var tenantId = TestIds.New("hrs-def");

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile/hours");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await get.ReadAsTextAsync());
        var hours = doc.RootElement.GetProperty("hours");
        Assert.Equal(7, hours.GetArrayLength());
        Assert.Equal("Monday", hours[0].GetProperty("d").GetString());
        Assert.Equal("09:00", hours[0].GetProperty("open").GetString());
        Assert.Equal("18:00", hours[0].GetProperty("close").GetString());
        Assert.True(hours[0].GetProperty("on").GetBoolean());
        Assert.Equal("Sunday", hours[6].GetProperty("d").GetString());
        Assert.False(hours[6].GetProperty("on").GetBoolean());
        Assert.True(hours[5].GetProperty("on").GetBoolean()); // Saturday
    }

    [Fact]
    public async Task Put_ValidSevenDays_ReadsBack()
    {
        var tenantId = TestIds.New("hrs-set");

        var put = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { hours = SevenDays() }).ToUrl("/api/tenant-profile/hours");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var putDoc = JsonDocument.Parse(await put.ReadAsTextAsync());
        Assert.Equal("10:00", putDoc.RootElement.GetProperty("hours")[2].GetProperty("open").GetString());
        Assert.Equal("Wednesday", putDoc.RootElement.GetProperty("hours")[2].GetProperty("d").GetString());

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile/hours");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var getDoc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.Equal("20:00", getDoc.RootElement.GetProperty("hours")[4].GetProperty("close").GetString());
        Assert.False(getDoc.RootElement.GetProperty("hours")[6].GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task Put_FewerThanSevenDays_Is400()
    {
        var tenantId = TestIds.New("hrs-6");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new
            {
                hours = new[]
                {
                    Day("Monday", "09:00", "18:00", true),
                    Day("Tuesday", "09:00", "18:00", true),
                    Day("Wednesday", "09:00", "18:00", true),
                    Day("Thursday", "09:00", "18:00", true),
                    Day("Friday", "09:00", "18:00", true),
                    Day("Saturday", "09:00", "18:00", true),
                },
            }).ToUrl("/api/tenant-profile/hours");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task NonOwnerCannotPut()
    {
        var tenantId = TestIds.New("hrs-role");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { hours = SevenDays() }).ToUrl("/api/tenant-profile/hours");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }
}
