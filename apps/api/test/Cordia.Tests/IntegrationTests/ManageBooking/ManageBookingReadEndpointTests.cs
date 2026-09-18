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
    public async Task FindManageableBookings_ExcludesCancelledBookings()
    {
        // A guest who has cancelled has nothing left to manage; handing the dead booking back
        // as a candidate made the agent re-target it on the next call.
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await SeedManageableBookingAsync(
            tenantId, "bk-gone", "CORD-GONE", "cust-gone", "+61400000009", BookingLifecycleStatus.Cancelled);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/bookings?phone_e164=%2B61400000009");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("none", body.GetProperty("status").GetString());
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
        string phoneE164,
        BookingLifecycleStatus lifecycle = BookingLifecycleStatus.Confirmed)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        // Kept as close to a REAL BookingRequest as the projection allows.
        //
        // This fixture used to also carry `booking_id`, `booking_date` and `approx_time`, none of
        // which the submission path can produce — so the suite was green while the endpoint did
        // not work (see GuestIdentityOnBookingTests, which drives POST /api/bookings instead of
        // hand-seeding). Those three are gone:
        //   • booking_id  — the operation row below already carries it, which is where a real
        //                   booking's id comes from.
        //   • booking_date / approx_time — CanonicalBookingPayload derives both from `starts_at`,
        //                   which IS a real BookingRequest field.
        // `customer` is now a real field too (spec 04 §4.2).
        //
        // TWO fields remain synthetic because nothing in the platform can supply them:
        //   • booking_reference — authoritative source is Booking.BookingReference, but
        //                         BookingReadRepository.BaseRows never joins `bookings`.
        //   • business_name     — has NO source at all: there is no tenant/business profile
        //                         entity and TenantContextView carries no display name.
        // Until spec 12 ratifies one, a candidate cannot be projected from a real payload.
        var payload = JsonSerializer.Serialize(new
        {
            operation = "create",
            service_id = "svc-spa",
            starts_at = "2026-07-10T19:00:00+00:00",
            party_size = 2,
            customer = new { phone_e164 = phoneE164 },
            // --- not producible by the submission path; see above ---
            booking_reference = bookingReference,
            business_name = "Serenity Spa",
        });

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingReference,
            ProviderReference = $"provider-{bookingId}",
            Version = 1,
            LifecycleStatus = lifecycle,
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
