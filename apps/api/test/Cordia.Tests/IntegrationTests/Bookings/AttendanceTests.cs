#pragma warning disable xUnit1051

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Tests.IntegrationTests.Bookings;

/// <summary>
/// G13: POST /api/Bookings/{id}/attendance records attended/no_show without changing lifecycle.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class AttendanceTests(Acceptance.AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task RecordsAttendanceAndRoundTripsOnTheBookingRow()
    {
        var tenantId = TestIds.New("att");
        var bookingId = TestIds.New("bk");
        await using (var ctx = fixture.CreateContext(tenantId))
            await SeedBookingAsync(ctx, tenantId, bookingId);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { attendance = "attended" }).ToUrl($"/api/Bookings/{bookingId}/attendance");
            Operator(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal(bookingId, doc.RootElement.GetProperty("booking_id").GetString());
        Assert.Equal("attended", doc.RootElement.GetProperty("attendance").GetString());

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var booking = ctx.Bookings.Single(b => b.TenantId == tenantId && b.BookingId == bookingId);
            Assert.Equal(AttendanceStatus.Attended, booking.Attendance);
            Assert.Equal(BookingLifecycleStatus.Confirmed, booking.LifecycleStatus);
        }
    }

    [Fact]
    public async Task RefusesWithoutActor()
    {
        var tenantId = TestIds.New("att403");
        var bookingId = TestIds.New("bk");
        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingAsync(ctx, tenantId, bookingId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { attendance = "no_show" }).ToUrl($"/api/Bookings/{bookingId}/attendance");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task UnknownBookingIs404()
    {
        var tenantId = TestIds.New("att404");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { attendance = "attended" }).ToUrl($"/api/Bookings/{TestIds.New("missing")}/attendance");
            Operator(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    private static void Operator(Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "console-operator");
        x.WithRequestHeader("X-Actor-Role", "operator");
        x.WithRequestHeader("X-Reason-Code", "guest arrived");
    }

    private static async Task SeedBookingAsync(ApplicationDbContext ctx, string tenantId, string bookingId)
    {
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingId,
            CustomerId = "cust-1",
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            Attendance = AttendanceStatus.Unknown,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
