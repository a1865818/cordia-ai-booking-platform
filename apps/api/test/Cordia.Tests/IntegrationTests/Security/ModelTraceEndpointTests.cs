#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Tests.IntegrationTests.Acceptance;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Model-trace endpoints (spec 13 §25.3 via the spec-14 boundary): agent-plane POST with
/// boundary re-sanitization; staff-only GET (403 without a role, 404 with one for an unknown
/// trace); tenant scope everywhere (Inv-13).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ModelTraceEndpointTests(AcceptanceApiFixture fixture)
{
    private static string TraceBody(string traceId, string content)
        => $$"""{"trace_id":"{{traceId}}","session_id":"sess-mt","kind":"prompt","content":"{{content}}"}""";

    [Fact]
    public async Task PostThenGet_RoundTrips_WithPanMaskedAtRest()
    {
        var tenantId = $"t13-mt-{Guid.NewGuid():N}";
        var traceId = $"trace-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TraceBody(traceId, "prompt with 4111 1111 1111 1111 inside"))
                .ToUrl("/api/model-traces");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var response = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/model-traces/{traceId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "incident_42");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var bodyText = await response.ReadAsTextAsync();
        using var doc = JsonDocument.Parse(bodyText);
        Assert.Equal("prompt", doc.RootElement.GetProperty("kind").GetString());
        Assert.DoesNotContain("4111 1111 1111 1111", bodyText);
        Assert.Contains("[REDACTED]", bodyText);
    }

    [Fact]
    public async Task Get_WithoutStaffRole_Returns403()
    {
        var tenantId = $"t13-mt403-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/model-traces/any-trace");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task Get_UnknownTrace_WithRole_Returns404()
    {
        var tenantId = $"t13-mt404-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/model-traces/does-not-exist");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "incident_42");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task CrossTenant_Get_CannotSeeAnotherTenantsTrace()
    {
        var tenantA = $"t13-mt-a-{Guid.NewGuid():N}";
        var tenantB = $"t13-mt-b-{Guid.NewGuid():N}";
        var traceId = $"trace-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TraceBody(traceId, "tenant A secret prompt")).ToUrl("/api/model-traces");
            x.WithRequestHeader("X-Tenant-Id", tenantA);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // Inv-13: tenant B's staff cannot read tenant A's trace (store path isolation).
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/model-traces/{traceId}");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.WithRequestHeader("X-Actor", "ops.b");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "snoop");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }
}
