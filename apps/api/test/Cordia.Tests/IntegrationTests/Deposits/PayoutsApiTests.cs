#pragma warning disable xUnit1051
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Cordia.Tests.IntegrationTests.Persistence;

namespace Cordia.Tests.IntegrationTests.Deposits;

/// <summary>HTTP tests for GET /api/payouts/summary (G2). No Stripe Connect.</summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class PayoutsApiTests(AcceptanceApiFixture fixture)
{
    private async Task SeedDepositAsync(
        string tenantId,
        DepositStatus status,
        int amountMinor)
    {
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var now = DateTimeOffset.UtcNow;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DepositSessions.Add(new DepositSession
            {
                TenantId = tenantId,
                DepositSessionId = TestIds.New("dep"),
                OperationId = operationId,
                AmountMinor = amountMinor,
                Currency = "AUD",
                Status = status,
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                PaidAt = status == DepositStatus.Paid ? now : null,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Summary_EmptyTenant_ZerosAndNotConnected()
    {
        var tenantId = TestIds.New("pay-empty");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/payouts/summary");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("held_minor").GetInt64());
        Assert.Equal(0, root.GetProperty("available_minor").GetInt64());
        Assert.Equal("AUD", root.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_date").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_amount_minor").ValueKind);
        Assert.Equal("Not connected", root.GetProperty("account").GetString());
    }

    [Fact]
    public async Task Summary_MatchesHeldAndCapturedSums()
    {
        var tenantId = TestIds.New("pay-sum");
        await SeedDepositAsync(tenantId, DepositStatus.Created, 2000);
        await SeedDepositAsync(tenantId, DepositStatus.LinkSent, 1500);
        await SeedDepositAsync(tenantId, DepositStatus.Paid, 4000);
        await SeedDepositAsync(tenantId, DepositStatus.Expired, 9999);
        await SeedDepositAsync(tenantId, DepositStatus.Refunded, 8888);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/payouts/summary");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;
        Assert.Equal(3500, root.GetProperty("held_minor").GetInt64());
        Assert.Equal(4000, root.GetProperty("available_minor").GetInt64());
        Assert.Equal("AUD", root.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_date").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_amount_minor").ValueKind);
        Assert.Equal("Not connected", root.GetProperty("account").GetString());
    }
}
