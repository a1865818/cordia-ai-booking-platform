#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// HTTP tests for GET/POST/PUT /api/agent-knowledge (docs/backend-gaps.md G9).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class AgentKnowledgeTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    [Fact]
    public async Task PostPutGet_ManualEntryRoundTrip()
    {
        var tenantId = TestIds.New("knw-rt");

        var created = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { q = "Where do I park?" }).ToUrl("/api/agent-knowledge");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var createdDoc = JsonDocument.Parse(await created.ReadAsTextAsync());
        var id = createdDoc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal("Where do I park?", createdDoc.RootElement.GetProperty("q").GetString());
        Assert.Equal("needs-answer", createdDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("manual", createdDoc.RootElement.GetProperty("source").GetString());

        var updated = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { a = "Free 2-hour parking on Gouger St." })
                .ToUrl($"/api/agent-knowledge/{id}");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var updatedDoc = JsonDocument.Parse(await updated.ReadAsTextAsync());
        Assert.Equal("Free 2-hour parking on Gouger St.", updatedDoc.RootElement.GetProperty("a").GetString());
        Assert.Equal("answered", updatedDoc.RootElement.GetProperty("status").GetString());

        var list = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/agent-knowledge");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var listDoc = JsonDocument.Parse(await list.ReadAsTextAsync());
        var entries = listDoc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal(id, entries[0].GetProperty("id").GetString());
        Assert.Equal("answered", entries[0].GetProperty("status").GetString());
        Assert.Equal("manual", entries[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Post_WithAnswer_IsAnswered()
    {
        var tenantId = TestIds.New("knw-ans");

        var created = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { q = "Do you take walk-ins?", a = "Yes, subject to chair availability." })
                .ToUrl("/api/agent-knowledge");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var doc = JsonDocument.Parse(await created.ReadAsTextAsync());
        Assert.Equal("answered", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NonOwnerCannotPost()
    {
        var tenantId = TestIds.New("knw-role");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { q = "Hijack?" }).ToUrl("/api/agent-knowledge");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }
}
