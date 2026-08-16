#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Campaigns;

/// <summary>
/// Acceptance coverage for G3 campaigns: awaiting is first-class, schedule requires owner
/// approval (g4), reject is terminal, operators cannot approve, and rows are tenant-scoped.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CampaignsApiTests(Acceptance.AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "campaign_review");
    }

    private static object CreateBody(
        string title = "Win back no-shows",
        string channel = "sms",
        int recipients = 8) => new
        {
            title,
            audience = "customers with a no-show in the last 60 days",
            channel,
            message = "Hi {name}, we miss you. Reply BOOK.",
            recipients,
        };

    private async Task<string> CreateAsync(string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(CreateBody()).ToUrl("/api/campaigns");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<JsonDocument> ListAsync(string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/campaigns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        return JsonDocument.Parse(await result.ReadAsTextAsync());
    }

    [Fact]
    public async Task Create_ThenList_ShowsAwaiting()
    {
        var tenantId = TestIds.New("camp-create");
        var id = await CreateAsync(tenantId);

        using var list = await ListAsync(tenantId);
        var campaigns = list.RootElement.GetProperty("campaigns");
        Assert.Equal(JsonValueKind.Array, campaigns.ValueKind);
        Assert.Equal(1, campaigns.GetArrayLength());

        var row = campaigns[0];
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Equal("awaiting", row.GetProperty("status").GetString());
        Assert.Equal("Holding for your approval", row.GetProperty("when").GetString());
        Assert.False(row.GetProperty("approved").GetBoolean());
        Assert.Equal("Win back no-shows", row.GetProperty("title").GetString());
        Assert.Equal(8, row.GetProperty("recipients").GetInt32());
        Assert.Equal("messaging", row.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task ScheduleWithoutApprove_Returns409()
    {
        var tenantId = TestIds.New("camp-noappr");
        var id = await CreateAsync(tenantId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { send_at = "2026-09-01T10:00:00Z" })
                .ToUrl($"/api/campaigns/{id}/schedule");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task ApproveThenSchedule_BecomesScheduled()
    {
        var tenantId = TestIds.New("camp-sched");
        var id = await CreateAsync(tenantId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{id}/approve");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { send_at = "2026-09-01T10:00:00Z" })
                .ToUrl($"/api/campaigns/{id}/schedule");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var list = await ListAsync(tenantId);
        var row = list.RootElement.GetProperty("campaigns")[0];
        Assert.Equal("scheduled", row.GetProperty("status").GetString());
        Assert.True(row.GetProperty("approved").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(row.GetProperty("when").GetString(), out var when));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), when);
    }

    [Fact]
    public async Task Reject_IsTerminal_FurtherApproveReturns409()
    {
        var tenantId = TestIds.New("camp-rej");
        var id = await CreateAsync(tenantId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{id}/reject");
            Owner(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var list = await ListAsync(tenantId);
        var row = list.RootElement.GetProperty("campaigns")[0];
        Assert.Equal("rejected", row.GetProperty("status").GetString());
        Assert.Equal("Rejected", row.GetProperty("when").GetString());

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{id}/approve");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task OperatorRole_CannotApprove()
    {
        var tenantId = TestIds.New("camp-role");
        var id = await CreateAsync(tenantId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{id}/approve");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task CrossTenant_IsInvisible()
    {
        var tenantA = TestIds.New("camp-xta");
        var tenantB = TestIds.New("camp-xtb");
        var id = await CreateAsync(tenantA);

        using var listB = await ListAsync(tenantB);
        Assert.Equal(0, listB.RootElement.GetProperty("campaigns").GetArrayLength());

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{id}/approve");
            Owner(x, tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task AudienceEstimate_ReturnsStoredRecipients()
    {
        var tenantId = TestIds.New("camp-est");
        var id = await CreateAsync(tenantId);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/campaigns/{id}/audience-estimate");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal(8, doc.RootElement.GetProperty("recipients").GetInt32());
    }

    [Fact]
    public async Task UnknownId_Returns404()
    {
        var tenantId = TestIds.New("camp-404");

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl("/api/campaigns/cp_missing/approve");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }
}
