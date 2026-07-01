#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.ManageBooking;

[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class VerificationEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task RequestVerification_CreatesChallengeAndDeduplicatesByKey()
    {
        var tenantId = $"t8-ver-req-{Guid.NewGuid():N}";
        var body = RequestBody("idem-req-1", "cust-req", "bk-req", "modify");
        await SeedBookingAsync(tenantId, "bk-req", "cust-req");

        var first = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var second = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var firstDoc = JsonDocument.Parse(await first.ReadAsTextAsync());
        using var secondDoc = JsonDocument.Parse(await second.ReadAsTextAsync());

        Assert.Equal("sent", firstDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("already_sent", secondDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            firstDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString(),
            secondDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString());
    }

    [Fact]
    public async Task CheckVerification_PendingBadCodeThenGranted_IssuesGrantAndAudit()
    {
        var tenantId = $"t8-ver-check-{Guid.NewGuid():N}";
        await SeedBookingAsync(tenantId, "bk-check", "cust-check");
        var request = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(RequestBody("idem-check-1", "cust-check", "bk-check", "cancel"))
                .ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var requestDoc = JsonDocument.Parse(await request.ReadAsTextAsync());
        var verificationId = requestDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString()!;

        var pending = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/verifications/{verificationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using (var pendingDoc = JsonDocument.Parse(await pending.ReadAsTextAsync()))
            Assert.Equal("pending", pendingDoc.RootElement.GetProperty("status").GetString());

        var bad = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("""{"code":"111111"}""").ToUrl($"/api/verifications/{verificationId}/verify");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using (var badDoc = JsonDocument.Parse(await bad.ReadAsTextAsync()))
            Assert.Equal("failed", badDoc.RootElement.GetProperty("status").GetString());

        var granted = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("""{"code":"000000"}""").ToUrl($"/api/verifications/{verificationId}/verify");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Correlation-Id", "corr-verification-test");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var grantedDoc = JsonDocument.Parse(await granted.ReadAsTextAsync());
        Assert.Equal("granted", grantedDoc.RootElement.GetProperty("status").GetString());
        var grant = grantedDoc.RootElement.GetProperty("grant");
        Assert.Equal("cust-check", grant.GetProperty("customer_id").GetString());
        Assert.Equal("bk-check", grant.GetProperty("target_booking_id").GetString());
        Assert.Equal("cancel", grant.GetProperty("allowed_action").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var grantId = grant.GetProperty("grant_id").GetString();
        var grantRow = await ctx.VerificationGrants.AsNoTracking()
            .SingleOrDefaultAsync(g => g.TenantId == tenantId && g.GrantId == grantId);
        Assert.NotNull(grantRow);
        Assert.Equal(VerificationActionEnum.Cancel, grantRow.AllowedAction);

        var audit = await ctx.AuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.EventType == "grant_issued");
        Assert.NotNull(audit);
        Assert.Equal("corr-verification-test", audit.CorrelationId);
    }

    private static string RequestBody(
        string idempotencyKey,
        string customerId,
        string bookingId,
        string action)
        => $$"""
        {
          "target_booking_id": "{{bookingId}}",
          "customer_id": "{{customerId}}",
          "channel": "sms",
          "action": "{{action}}",
          "verification_idempotency_key": "{{idempotencyKey}}"
        }
        """;

    private async Task SeedBookingAsync(string tenantId, string bookingId, string customerId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"REF-{bookingId}",
            ProviderReference = $"provider-{bookingId}",
            Version = 0,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();
    }
}
