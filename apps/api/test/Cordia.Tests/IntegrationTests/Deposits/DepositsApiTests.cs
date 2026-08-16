#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Cordia.Tests.IntegrationTests.Persistence;

namespace Cordia.Tests.IntegrationTests.Deposits;

/// <summary>HTTP tests for GET /api/deposits and POST /api/deposits/{id}/refund (G2).</summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class DepositsApiTests(AcceptanceApiFixture fixture)
{
    private static void Operator(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "ops@example.com");
        x.WithRequestHeader("X-Actor-Role", "operator");
        x.WithRequestHeader("X-Reason-Code", "guest cancelled");
    }

    private async Task<string> SeedDepositAsync(
        string tenantId,
        DepositStatus status,
        int amountMinor,
        DateTimeOffset createdAt,
        DateTimeOffset? paidAt = null)
    {
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var depositId = TestIds.New("dep");
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DepositSessions.Add(new DepositSession
            {
                TenantId = tenantId,
                DepositSessionId = depositId,
                OperationId = operationId,
                AmountMinor = amountMinor,
                Currency = "AUD",
                Status = status,
                ExpiresAt = createdAt.AddHours(1),
                CreatedAt = createdAt,
                PaidAt = paidAt,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        return depositId;
    }

    [Fact]
    public async Task List_EmptyTenant_ReturnsEmptyArray()
    {
        var tenantId = TestIds.New("dep-empty");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposits");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var deposits = doc.RootElement.GetProperty("deposits");
        Assert.Equal(JsonValueKind.Array, deposits.ValueKind);
        Assert.Equal(0, deposits.GetArrayLength());
    }

    [Fact]
    public async Task List_SeededSession_ReturnsIt_StatusFilterWorks()
    {
        var tenantId = TestIds.New("dep-list");
        var now = DateTimeOffset.UtcNow;
        var createdId = await SeedDepositAsync(tenantId, DepositStatus.Created, 2000, now.AddMinutes(-2));
        var paidId = await SeedDepositAsync(
            tenantId, DepositStatus.Paid, 3500, now.AddMinutes(-1), paidAt: now);

        var all = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposits");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using (var allDoc = JsonDocument.Parse(await all.ReadAsTextAsync()))
        {
            var deposits = allDoc.RootElement.GetProperty("deposits");
            Assert.Equal(2, deposits.GetArrayLength());
            var ids = deposits.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
            Assert.Contains(createdId, ids);
            Assert.Contains(paidId, ids);

            var paid = deposits.EnumerateArray().Single(e => e.GetProperty("id").GetString() == paidId);
            Assert.Equal("paid", paid.GetProperty("status").GetString());
            Assert.Equal(3500, paid.GetProperty("amount_minor").GetInt32());
            Assert.Equal("AUD", paid.GetProperty("currency").GetString());
            Assert.False(paid.TryGetProperty("service_id", out _));
        }

        var held = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposits?status=held");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using (var heldDoc = JsonDocument.Parse(await held.ReadAsTextAsync()))
        {
            var deposits = heldDoc.RootElement.GetProperty("deposits");
            Assert.Equal(1, deposits.GetArrayLength());
            Assert.Equal(createdId, deposits[0].GetProperty("id").GetString());
            Assert.Equal("created", deposits[0].GetProperty("status").GetString());
        }

        var captured = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/deposits?status=captured");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var capturedDoc = JsonDocument.Parse(await captured.ReadAsTextAsync());
        var capturedDeposits = capturedDoc.RootElement.GetProperty("deposits");
        Assert.Equal(1, capturedDeposits.GetArrayLength());
        Assert.Equal(paidId, capturedDeposits[0].GetProperty("id").GetString());
        Assert.Equal("paid", capturedDeposits[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refund_Paid_ReturnsRefunded()
    {
        var tenantId = TestIds.New("dep-ref");
        var now = DateTimeOffset.UtcNow;
        var id = await SeedDepositAsync(tenantId, DepositStatus.Paid, 2000, now, paidAt: now);

        var refund = await fixture.Host.Scenario(x =>
        {
            x.Post.Url($"/api/deposits/{id}/refund");
            Operator(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await refund.ReadAsTextAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("refunded", doc.RootElement.GetProperty("status").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.DepositSessions.FindAsync(tenantId, id);
        Assert.NotNull(row);
        Assert.Equal(DepositStatus.Refunded, row.Status);
    }

    [Fact]
    public async Task Refund_Created_Returns409()
    {
        var tenantId = TestIds.New("dep-409");
        var id = await SeedDepositAsync(tenantId, DepositStatus.Created, 2000, DateTimeOffset.UtcNow);

        await fixture.Host.Scenario(x =>
        {
            x.Post.Url($"/api/deposits/{id}/refund");
            Operator(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Refund_Unknown_Returns404()
    {
        var tenantId = TestIds.New("dep-404");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Url($"/api/deposits/{TestIds.New("missing")}/refund");
            Operator(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }
}
