#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Tests.IntegrationTests.Reporting;

/// <summary>
/// G14: GET /api/reporting/kpis — bookings is always available; noshow/ai stay unavailable
/// until attendance / attribution actually exist. Revenue of 0 is honest, not missing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class GetKpisTests(Acceptance.AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task BookedOperation_BookingsAvailable_NoshowAndAiUnavailableWhenUnattributed()
    {
        var tenantId = TestIds.New("kpi");
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookedAsync(ctx, tenantId, now);

        var from = now.AddHours(-1);
        var to = now.AddHours(1);
        var root = await GetJsonAsync(
            $"/api/reporting/kpis?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}",
            tenantId);

        Assert.True(root.TryGetProperty("prior_from", out _));
        Assert.True(root.TryGetProperty("prior_to", out _));

        var kpis = root.GetProperty("kpis").EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!, e => e);

        var bookings = kpis["bookings"];
        Assert.True(bookings.GetProperty("available").GetBoolean());
        Assert.Equal("count", bookings.GetProperty("unit").GetString());
        Assert.Equal(1, bookings.GetProperty("value").GetDouble());

        Assert.False(kpis["noshow"].GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kpis["noshow"].GetProperty("value").ValueKind);

        Assert.False(kpis["ai"].GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kpis["ai"].GetProperty("value").ValueKind);

        var rev = kpis["rev"];
        Assert.True(rev.GetProperty("available").GetBoolean());
        Assert.Equal("minor", rev.GetProperty("unit").GetString());
        Assert.Equal(0, rev.GetProperty("value").GetDouble());

        Assert.Equal(JsonValueKind.Null, root.GetProperty("handled").ValueKind);
    }

    [Fact]
    public async Task MissingTenant_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/reporting/kpis");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task DefaultWindow_Returns200()
    {
        var tenantId = TestIds.New("kpi-def");
        var root = await GetJsonAsync("/api/reporting/kpis", tenantId);

        var bookings = root.GetProperty("kpis").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "bookings");
        Assert.True(bookings.GetProperty("available").GetBoolean());
        Assert.Equal(0, bookings.GetProperty("value").GetDouble());
    }

    private async Task<JsonElement> GetJsonAsync(string url, string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
    }

    private static async Task SeedBookedAsync(ApplicationDbContext ctx, string tenantId, DateTimeOffset at)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = $"op-{Guid.NewGuid():N}",
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Booked,
            Version = 0,
            CreatedAt = at,
            UpdatedAt = at,
        });
        await ctx.SaveChangesAsync();
    }
}
