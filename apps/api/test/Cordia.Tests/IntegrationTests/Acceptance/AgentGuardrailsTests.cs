#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// HTTP tests for GET/PUT /api/agent-policy/guardrails (docs/backend-gaps.md G8).
/// Locked rails (g1, g4) stay on even when the client sends false.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class AgentGuardrailsTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    [Fact]
    public async Task Get_MissingRow_ReturnsCanonicalDefaults()
    {
        var tenantId = TestIds.New("gds-def");

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/agent-policy/guardrails");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await get.ReadAsTextAsync());
        var rails = doc.RootElement.GetProperty("guardrails");
        Assert.Equal(6, rails.GetArrayLength());

        var byId = rails.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.True(byId["g1"].GetProperty("on").GetBoolean());
        Assert.True(byId["g1"].GetProperty("locked").GetBoolean());
        Assert.True(byId["g4"].GetProperty("on").GetBoolean());
        Assert.True(byId["g4"].GetProperty("locked").GetBoolean());
        Assert.True(byId["g5"].GetProperty("on").GetBoolean());
        Assert.False(byId["g6"].GetProperty("on").GetBoolean());
        Assert.False(byId["g6"].GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Put_G1False_StillReturnsOnAndLocked()
    {
        var tenantId = TestIds.New("gds-lock");

        var put = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new
            {
                guardrails = new[]
                {
                    new { id = "g1", label = "ignored", on = false, locked = false },
                    new { id = "g6", label = "ignored", on = true, locked = false },
                },
            }).ToUrl("/api/agent-policy/guardrails");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await put.ReadAsTextAsync());
        var byId = doc.RootElement.GetProperty("guardrails")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!);

        Assert.True(byId["g1"].GetProperty("on").GetBoolean());
        Assert.True(byId["g1"].GetProperty("locked").GetBoolean());
        Assert.Equal("Never book without reading the details back", byId["g1"].GetProperty("label").GetString());
        Assert.True(byId["g6"].GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task NonOwnerCannotPut()
    {
        var tenantId = TestIds.New("gds-role");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new
            {
                guardrails = new[] { new { id = "g6", on = true } },
            }).ToUrl("/api/agent-policy/guardrails");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }
}
