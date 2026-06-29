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
public class ManageBookingReadEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task FindManageableBookings_ReturnsMinimizedCandidates()
    {
        var tenantId = $"t8-http-find-{Guid.NewGuid():N}";
        await SeedManageableBookingAsync(tenantId, "bk-find", "CORD-FIND", "cust-find", "+61400000000");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings?phone_e164=%2B61400000000");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("found", doc.RootElement.GetProperty("status").GetString());
        var candidate = doc.RootElement.GetProperty("candidates")[0];
        Assert.Equal("bk-find", candidate.GetProperty("booking_id").GetString());
        Assert.Equal("CORD-FIND", candidate.GetProperty("booking_reference").GetString());
        Assert.False(candidate.TryGetProperty("customer", out _));
        Assert.False(candidate.TryGetProperty("contact_phone", out _));
    }

    [Fact]
    public async Task FindManageableBookings_MissingHints_Returns400()
    {
        var tenantId = $"t8-http-hints-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task FindManageableBookings_MissingTenant_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings?booking_reference=CORD-MISSING-TENANT");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task GetBookingDetails_WithoutGrant_ReturnsMinimalProjection()
    {
        var tenantId = $"t8-http-detail-{Guid.NewGuid():N}";
        await SeedManageableBookingAsync(tenantId, "bk-detail", "CORD-DETAIL", "cust-detail", "+61400000001");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings/bk-detail");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        var booking = doc.RootElement.GetProperty("booking");
        Assert.Equal("bk-detail", booking.GetProperty("booking_id").GetString());
        Assert.Equal("CORD-DETAIL", booking.GetProperty("booking_reference").GetString());
        Assert.Equal("confirmed", booking.GetProperty("status").GetString());
        Assert.False(booking.TryGetProperty("customer_id", out _));
    }

    [Fact]
    public async Task GetBookingDetails_WrongTenant_Returns404()
    {
        var tenantA = $"t8-http-detail-a-{Guid.NewGuid():N}";
        var tenantB = $"t8-http-detail-b-{Guid.NewGuid():N}";
        await SeedManageableBookingAsync(tenantA, "bk-private", "CORD-PRIVATE", "cust-private", "+61400000002");

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings/bk-private");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    private async Task SeedManageableBookingAsync(
        string tenantId,
        string bookingId,
        string bookingReference,
        string customerId,
        string phoneE164)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            booking_id = bookingId,
            booking_reference = bookingReference,
            business_name = "Serenity Spa",
            booking_date = "2026-07-10",
            approx_time = "19:00",
            party_size = 2,
            customer = new { phone_e164 = phoneE164 },
        });

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingReference,
            ProviderReference = $"provider-{bookingId}",
            Version = 1,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
            CreatedAt = now,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = payload,
            PayloadHash = $"h-{Guid.NewGuid():N}",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            CreatedAt = now,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Booked,
            BookingId = bookingId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        });
        await ctx.SaveChangesAsync();
    }
}
