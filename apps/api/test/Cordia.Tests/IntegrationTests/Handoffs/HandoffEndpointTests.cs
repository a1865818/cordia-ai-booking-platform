#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Tests.IntegrationTests.Acceptance;

namespace Cordia.Tests.IntegrationTests.Handoffs;

/// <summary>
/// HTTP-level tests for <c>POST /api/handoffs</c> (spec 03 §4.3).
///
/// This route answered 405 until now; the agent's <c>request_human_handoff</c>
/// tool degraded gracefully, which is acceptable on a transcript and not on a
/// live call. The wire shape asserted here is exactly what the Python
/// <c>HandoffResult</c> contract parses — it forbids unknown keys, so a drift
/// in either direction fails.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class HandoffEndpointTests(AcceptanceApiFixture fixture)
{
    private static object Body(string key, string reason = "verification_failed") => new
    {
        session_id = "sess-handoff-1",
        reason_code = reason,
        handoff_idempotency_key = key,
        recommended_action = (string?)null,
    };

    [Fact]
    public async Task Handoff_WithoutTenantHeader_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body($"k-{Guid.NewGuid():N}")).ToUrl("/api/handoffs");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task Handoff_CreatesAWorkItemInTheExpectedWireShape()
    {
        var tenantId = $"t-handoff-{Guid.NewGuid():N}";

        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body($"k-{Guid.NewGuid():N}")).ToUrl("/api/handoffs");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        var root = doc.RootElement;

        Assert.Equal("created", root.GetProperty("status").GetString());
        Assert.Equal("default", root.GetProperty("queue").GetString());
        Assert.StartsWith("hwi_", root.GetProperty("work_item_id").GetString());

        // HandoffResult forbids extra keys on the Python side.
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Handoff_RepeatedWithTheSameKey_ReturnsTheSameWorkItem()
    {
        var tenantId = $"t-handoff-{Guid.NewGuid():N}";
        var key = $"k-{Guid.NewGuid():N}";

        var first = await Post(tenantId, key);
        var second = await Post(tenantId, key);

        Assert.Equal("created", first.GetProperty("status").GetString());
        Assert.Equal("existing", second.GetProperty("status").GetString());
        Assert.Equal(
            first.GetProperty("work_item_id").GetString(),
            second.GetProperty("work_item_id").GetString());
    }

    [Fact]
    public async Task Handoff_SameKeyInAnotherTenant_IsASeparateEscalation()
    {
        var key = $"k-{Guid.NewGuid():N}";

        var mine = await Post($"t-handoff-a-{Guid.NewGuid():N}", key);
        var theirs = await Post($"t-handoff-b-{Guid.NewGuid():N}", key);

        Assert.Equal("created", theirs.GetProperty("status").GetString());
        Assert.NotEqual(
            mine.GetProperty("work_item_id").GetString(),
            theirs.GetProperty("work_item_id").GetString());
    }

    [Fact]
    public async Task Handoff_WithoutADedupKey_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new
            {
                session_id = "sess-handoff-1",
                reason_code = "complaint",
                handoff_idempotency_key = "",
            }).ToUrl("/api/handoffs");
            x.WithRequestHeader("X-Tenant-Id", $"t-handoff-{Guid.NewGuid():N}");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    private async Task<JsonElement> Post(string tenantId, string key)
    {
        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body(key)).ToUrl("/api/handoffs");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return JsonDocument.Parse(await response.ReadAsTextAsync()).RootElement.Clone();
    }
}
