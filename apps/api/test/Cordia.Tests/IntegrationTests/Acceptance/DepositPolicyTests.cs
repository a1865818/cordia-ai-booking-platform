#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// HTTP tests for GET/PUT /api/deposit-policy (docs/backend-gaps.md G7).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class DepositPolicyTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    [Fact]
    public async Task Get_MissingRow_ReturnsDefaults()
    {
        var tenantId = TestIds.New("dep-def");

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposit-policy");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.False(doc.RootElement.GetProperty("require").GetBoolean());
        Assert.Equal("per_service", doc.RootElement.GetProperty("amount_policy").GetString());
        Assert.False(doc.RootElement.GetProperty("no_show_only").GetBoolean());
        Assert.Equal("24 hours", doc.RootElement.GetProperty("refund_window").GetString());
        Assert.Equal("AUD", doc.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task PutAndGet_RoundTrip()
    {
        var tenantId = TestIds.New("dep-set");

        var put = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new
            {
                require = true,
                amount_policy = "flat",
                no_show_only = true,
                refund_window = "48 hours",
                amount_minor = 5000,
                currency = "AUD",
            }).ToUrl("/api/deposit-policy");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using (var putDoc = JsonDocument.Parse(await put.ReadAsTextAsync()))
        {
            Assert.True(putDoc.RootElement.GetProperty("require").GetBoolean());
            Assert.Equal("flat", putDoc.RootElement.GetProperty("amount_policy").GetString());
            Assert.True(putDoc.RootElement.GetProperty("no_show_only").GetBoolean());
            Assert.Equal("48 hours", putDoc.RootElement.GetProperty("refund_window").GetString());
            Assert.Equal(5000, putDoc.RootElement.GetProperty("amount_minor").GetInt64());
        }

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposit-policy");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var getDoc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.True(getDoc.RootElement.GetProperty("require").GetBoolean());
        Assert.Equal("flat", getDoc.RootElement.GetProperty("amount_policy").GetString());
        Assert.Equal(5000, getDoc.RootElement.GetProperty("amount_minor").GetInt64());
    }

    [Fact]
    public async Task NonOwnerCannotPut()
    {
        var tenantId = TestIds.New("dep-role");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new
            {
                require = true,
                amount_policy = "per_service",
                no_show_only = false,
                refund_window = "24 hours",
            }).ToUrl("/api/deposit-policy");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }
}
