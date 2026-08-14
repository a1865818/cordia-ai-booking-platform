#pragma warning disable xUnit1051

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;

namespace Cordia.Tests.IntegrationTests.Resources;

/// <summary>
/// G11: GET /api/resources lists the roster (including inactive); POST is owner-gated.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ResourceTests(Acceptance.AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task OwnerCanCreateAndListIncludesInactive()
    {
        var tenantId = TestIds.New("res");

        var created = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { name = "Alex Chen", role = "stylist" }).ToUrl("/api/resources");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var createdDoc = JsonDocument.Parse(await created.ReadAsTextAsync());
        Assert.Equal("Alex Chen", createdDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("stylist", createdDoc.RootElement.GetProperty("role").GetString());
        Assert.True(createdDoc.RootElement.GetProperty("active").GetBoolean());
        var resourceId = createdDoc.RootElement.GetProperty("resource_id").GetString();
        Assert.StartsWith("staff_", resourceId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.StaffResources.Add(new StaffResource
            {
                TenantId = tenantId,
                ResourceId = "staff_inactive",
                Name = "Blair",
                Active = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var listed = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/resources");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var listDoc = JsonDocument.Parse(await listed.ReadAsTextAsync());
        var resources = listDoc.RootElement.GetProperty("resources").EnumerateArray().ToList();
        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, r => r.GetProperty("resource_id").GetString() == resourceId
            && r.GetProperty("active").GetBoolean());
        Assert.Contains(resources, r => r.GetProperty("resource_id").GetString() == "staff_inactive"
            && r.GetProperty("active").GetBoolean() == false);
    }

    [Fact]
    public async Task OperatorCannotCreate()
    {
        var tenantId = TestIds.New("res-op");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { name = "Hijack" }).ToUrl("/api/resources");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    private static void Owner(Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "roster setup");
    }
}
