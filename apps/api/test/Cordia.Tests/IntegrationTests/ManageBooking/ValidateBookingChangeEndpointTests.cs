#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Tests.IntegrationTests.Acceptance;

namespace Cordia.Tests.IntegrationTests.ManageBooking;

[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ValidateBookingChangeEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task ValidateChange_WithValidGrantAndVersion_ReturnsValid()
    {
        var tenantId = $"t8-validate-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-valid", "cust-valid", "grant-valid", VerificationActionEnum.Modify);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-valid", 3, "grant-valid")).ToUrl("/api/bookings/bk-valid/changes:validate");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        var validation = doc.RootElement.GetProperty("validation");
        Assert.True(validation.GetProperty("valid").GetBoolean());
        Assert.Equal("valid", validation.GetProperty("code").GetString());
        Assert.True(validation.GetProperty("requires_confirmation").GetBoolean());
    }

    [Fact]
    public async Task ValidateChange_StaleVersion_ReturnsStaleVersion()
    {
        var tenantId = $"t8-validate-stale-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-stale", "cust-stale", "grant-stale", VerificationActionEnum.Modify);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-stale", 2, "grant-stale")).ToUrl("/api/bookings/bk-stale/changes:validate");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("stale_version", doc.RootElement.GetProperty("status").GetString());
        var validation = doc.RootElement.GetProperty("validation");
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Equal("stale_version", validation.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidateChange_WrongGrantAction_Returns403()
    {
        var tenantId = $"t8-validate-grant-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-grant", "cust-grant", "grant-cancel", VerificationActionEnum.Cancel);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-grant", 3, "grant-cancel")).ToUrl("/api/bookings/bk-grant/changes:validate");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    private async Task SeedBookingAndGrantAsync(
        string tenantId,
        string bookingId,
        string customerId,
        string grantId,
        VerificationActionEnum grantAction)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"CORD-{bookingId}",
            Version = 3,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        ctx.VerificationGrants.Add(new VerificationGrant
        {
            TenantId = tenantId,
            GrantId = grantId,
            CustomerRef = customerId,
            TargetBookingId = bookingId,
            AllowedAction = grantAction,
            VerificationLevel = VerificationLevel.IdentityVerified,
            SessionOrOperationRef = bookingId,
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(15),
        });
        await ctx.SaveChangesAsync();
    }

    private static string Body(string bookingId, long version, string grantId)
        => $$"""
        {
          "target_booking_id": "{{bookingId}}",
          "expected_booking_version": {{version}},
          "requested_fields": { "party_size": 3 },
          "grant_id": "{{grantId}}"
        }
        """;
}
