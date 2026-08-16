#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Tests.IntegrationTests.Console;

/// <summary>
/// G15: GET /api/customers/{ref} and PUT /api/customers/{ref}/notes.
/// Notes round-trip; missing actor is 403; origin/no_show_count are derived, never invented.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CustomerDetailTests(Acceptance.AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task NotesPutGet_RoundTrip()
    {
        var tenantId = TestIds.New("cust");
        var customerRef = "cust-notes";
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookedCustomerAsync(ctx, tenantId, "bk-notes", customerRef, now);

        var put = await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { note = "prefers window table", tags = new[] { "vip", "window" } })
                .ToUrl($"/api/customers/{customerRef}/notes");
            Operator(x, tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var putDoc = JsonDocument.Parse(await put.ReadAsTextAsync());
        Assert.Equal(customerRef, putDoc.RootElement.GetProperty("customer_ref").GetString());
        Assert.Equal("prefers window table", putDoc.RootElement.GetProperty("note").GetString());
        Assert.Equal(
            new[] { "vip", "window" },
            putDoc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal(1, putDoc.RootElement.GetProperty("booking_count").GetInt32());
        Assert.Equal(0, putDoc.RootElement.GetProperty("no_show_count").GetInt32());

        var get = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/customers/{customerRef}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var getDoc = JsonDocument.Parse(await get.ReadAsTextAsync());
        Assert.Equal("prefers window table", getDoc.RootElement.GetProperty("note").GetString());
        Assert.Equal(
            new[] { "vip", "window" },
            getDoc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.False(getDoc.RootElement.TryGetProperty("phone", out _));
        Assert.False(getDoc.RootElement.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task NotesPut_WithoutActor_Is403()
    {
        var tenantId = TestIds.New("cust403");
        var customerRef = "cust-noactor";

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookedCustomerAsync(ctx, tenantId, "bk-403", customerRef, DateTimeOffset.UtcNow);

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { note = "should not stick" }).ToUrl($"/api/customers/{customerRef}/notes");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task UnknownCustomer_Is404()
    {
        var tenantId = TestIds.New("cust404");

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/customers/nobody-here");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    private static void Operator(Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "console-operator");
        x.WithRequestHeader("X-Actor-Role", "operator");
        x.WithRequestHeader("X-Reason-Code", "guest preference");
    }

    private static async Task SeedBookedCustomerAsync(
        ApplicationDbContext ctx, string tenantId, string bookingId, string customerRef, DateTimeOffset at)
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
            CanonicalPayload = JsonSerializer.Serialize(new
            {
                operation = "create",
                booking_id = bookingId,
                service_id = "gel",
                starts_at = at.ToString("o"),
                party_size = 2,
                duration_minutes = 45,
            }),
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
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingId,
            CustomerId = customerRef,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            Attendance = AttendanceStatus.Unknown,
            Version = 0,
            CreatedAt = at,
            UpdatedAt = at,
        });
        await ctx.SaveChangesAsync();
    }
}
