#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// HTTP-level tests for <c>POST /api/availability/candidates</c> (spec 03 §4.3).
/// Asserts the exact wire shape the agent's typed tool client parses
/// (<c>AvailabilityResult</c> flattened next to <c>status</c>) and the hold
/// idempotency contract.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CheckCandidateAvailabilityEndpointTests(AcceptanceApiFixture fixture)
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 8, 10, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Candidates_WithoutTenantHeader_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/api/availability/candidates");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task Candidates_UntrackedResource_ReturnsInformationalCandidate()
    {
        var tenantId = $"t-avail-untracked-{Guid.NewGuid():N}";

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/api/availability/candidates");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        Assert.Equal("available", root.GetProperty("status").GetString());
        Assert.Equal("informational", root.GetProperty("mode").GetString());
        Assert.Empty(root.GetProperty("invalid_fields").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("hold").ValueKind);

        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray().ToList());
        Assert.Equal(RequestedAt, candidate.GetProperty("starts_at").GetDateTimeOffset());
        Assert.Equal(90, candidate.GetProperty("duration_minutes").GetInt32());
        Assert.Equal("informational", candidate.GetProperty("availability_mode").GetString());
    }

    [Fact]
    public async Task Candidates_MissingStartsAt_ReportsRecoverableInvalidField()
    {
        var tenantId = $"t-avail-invalid-{Guid.NewGuid():N}";

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body(includeStartsAt: false)).ToUrl("/api/availability/candidates");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        Assert.Equal("unavailable", root.GetProperty("status").GetString());
        Assert.Contains(
            "starts_at",
            root.GetProperty("invalid_fields").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Candidates_TrackedSlotWithoutHold_ReturnsLiveAndWritesNoHold()
    {
        var tenantId = $"t-avail-live-{Guid.NewGuid():N}";
        await SeedSlotAsync(tenantId, totalCapacity: 20);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/api/availability/candidates");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("available", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("live", doc.RootElement.GetProperty("mode").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.Empty(await ctx.CapacityHolds.IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task Candidates_WantHoldTwiceWithSameKey_ReturnsSameHoldAndWritesOneRow()
    {
        var tenantId = $"t-avail-hold-{Guid.NewGuid():N}";
        await SeedSlotAsync(tenantId, totalCapacity: 20);
        var idempotencyKey = $"hold-idem-{Guid.NewGuid():N}";

        var first = await PostWithHoldAsync(tenantId, idempotencyKey);
        var second = await PostWithHoldAsync(tenantId, idempotencyKey);

        Assert.Equal("held", first.GetProperty("status").GetString());
        Assert.Equal("held", second.GetProperty("status").GetString());
        Assert.Equal("held", first.GetProperty("mode").GetString());

        var firstHoldId = first.GetProperty("hold").GetProperty("hold_id").GetString();
        var secondHoldId = second.GetProperty("hold").GetProperty("hold_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstHoldId));
        Assert.Equal(firstHoldId, secondHoldId);

        await using var ctx = fixture.CreateContext(tenantId);
        var holds = await ctx.CapacityHolds.IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId).ToListAsync();
        var hold = Assert.Single(holds);
        Assert.Equal(idempotencyKey, hold.OperationId);
        Assert.Equal(HoldStatus.Active, hold.Status);
        Assert.Equal(4, hold.ReservedCapacity);

        // The slot's held capacity was incremented exactly once (Inv-6).
        var slot = await ctx.CapacitySlots.IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(4, slot.HeldCapacity);
    }

    [Fact]
    public async Task Candidates_WantHoldWithoutIdempotencyKey_Returns400()
    {
        var tenantId = $"t-avail-nokey-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body(wantHold: true)).ToUrl("/api/availability/candidates");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> PostWithHoldAsync(string tenantId, string idempotencyKey)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(Body(wantHold: true)).ToUrl("/api/availability/candidates");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Idempotency-Key", idempotencyKey);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
    }

    private async Task SeedSlotAsync(string tenantId, int totalCapacity)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = $"svc_001|{RequestedAt:O}",
            ResourceId = "svc_001",
            StartsAt = RequestedAt,
            TotalCapacity = totalCapacity,
            HeldCapacity = 0,
            BookedCapacity = 0,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
    }

    private static object Body(bool wantHold = false, bool includeStartsAt = true)
    {
        var request = new Dictionary<string, object?>
        {
            ["operation"] = "create",
            ["service_id"] = "svc_001",
            ["location_id"] = "loc_001",
            ["party_size"] = 4,
            ["duration_minutes"] = 90,
        };

        if (includeStartsAt)
            request["starts_at"] = RequestedAt.ToString("O");

        return new Dictionary<string, object?>
        {
            ["draft"] = new Dictionary<string, object?>
            {
                ["tenant_id"] = "ignored-body-tenant",
                ["session_id"] = "sess-avail-int",
                ["schema_version"] = "1.0.0",
                ["tenant_policy_version"] = "policy.v1",
                ["source"] = new Dictionary<string, object?>
                {
                    ["channel"] = "voice",
                    ["language"] = "en-AU",
                },
                ["request"] = request,
            },
            ["want_hold"] = wantHold,
        };
    }
}
