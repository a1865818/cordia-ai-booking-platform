#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Console;

/// <summary>
/// The console's read of notification delivery state (#83, spec 10). It must report what the
/// table stores — never a synthesised "sent" — and the console must have no way to assert a
/// receipt of its own.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class NotificationDeliveryReadTests(AcceptanceApiFixture fixture)
{
    private async Task<string> SeedOperationForBookingAsync(string tenantId, string? bookingId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await TestData.SeedOperationAsync(ctx, tenantId);
        if (bookingId is not null)
        {
            await ctx.BookingOperations
                .Where(o => o.TenantId == tenantId && o.OperationId == operationId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.BookingId, bookingId));
        }
        return operationId;
    }

    private async Task<string> SeedNotificationAsync(
        string tenantId,
        string operationId,
        NotificationEvent eventType,
        NotificationState state,
        string? failureCategory = null,
        DateTimeOffset? createdAt = null)
    {
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        var at = createdAt ?? DateTimeOffset.UtcNow;
        ctx.Notifications.Add(new Notification
        {
            TenantId = tenantId,
            NotificationId = notificationId,
            OperationId = operationId,
            EventType = eventType,
            RecipientRef = "+61400000000",
            TemplateVersion = "v1",
            MaterialRevision = 1,
            State = state,
            FailureCategory = failureCategory,
            Channel = NotificationChannel.Sms,
            Priority = MessagePriority.High,
            AttemptCount = 2,
            CreatedAt = at,
            UpdatedAt = at,
            Version = 1,
        });
        await ctx.SaveChangesAsync();
        return notificationId;
    }

    private async Task<List<JsonElement>> ReadAsync(string url, string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var root = JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
        return root.GetProperty("notifications").EnumerateArray().ToList();
    }

    [Fact]
    public async Task ABookingsRead_ShowsWhatWasDelivered_AndWhatFailed_AcrossEveryOperationOnIt()
    {
        var tenantId = $"t-notif-read-{Guid.NewGuid():N}";
        var bookingId = $"bk-{Guid.NewGuid():N}";
        var createOp = await SeedOperationForBookingAsync(tenantId, bookingId);
        var cancelOp = await SeedOperationForBookingAsync(tenantId, bookingId);
        // Same tenant, a different booking: its notification must not bleed into this read.
        var unrelatedOp = await SeedOperationForBookingAsync(tenantId, $"bk-other-{Guid.NewGuid():N}");
        await SeedNotificationAsync(tenantId, unrelatedOp, NotificationEvent.BookingConfirmed, NotificationState.Delivered);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var confirmed = await SeedNotificationAsync(
            tenantId, createOp, NotificationEvent.BookingConfirmed, NotificationState.Delivered, createdAt: t0);
        var bounced = await SeedNotificationAsync(
            tenantId, cancelOp, NotificationEvent.RequestAccepted, NotificationState.FailedPermanent,
            failureCategory: "bounce", createdAt: t0.AddMinutes(5));

        var rows = await ReadAsync($"/api/Bookings/{bookingId}/notifications", tenantId);

        Assert.Equal([confirmed, bounced], rows.Select(r => r.GetProperty("notification_id").GetString()));
        Assert.Equal("delivered", rows[0].GetProperty("state").GetString());
        Assert.Equal("booking_confirmed", rows[0].GetProperty("event_type").GetString());
        Assert.Equal("failed_permanent", rows[1].GetProperty("state").GetString());
        Assert.Equal("bounce", rows[1].GetProperty("failure_category").GetString());
    }

    /// <summary>The destination is contact PII; the console needs the outcome, not the address.</summary>
    [Fact]
    public async Task TheRead_NeverCarriesTheRecipient()
    {
        var tenantId = $"t-notif-pii-{Guid.NewGuid():N}";
        var bookingId = $"bk-{Guid.NewGuid():N}";
        var op = await SeedOperationForBookingAsync(tenantId, bookingId);
        await SeedNotificationAsync(tenantId, op, NotificationEvent.BookingConfirmed, NotificationState.Accepted);

        var rows = await ReadAsync($"/api/Bookings/{bookingId}/notifications", tenantId);

        var row = Assert.Single(rows);
        Assert.False(row.TryGetProperty("recipient_ref", out _));
        Assert.DoesNotContain("+61400000000", row.GetRawText());
    }

    /// <summary>A booking with no notification row reads as empty — never as "sent".</summary>
    [Fact]
    public async Task ABookingWithNoNotifications_ReadsAsEmpty()
    {
        var tenantId = $"t-notif-empty-{Guid.NewGuid():N}";
        var bookingId = $"bk-{Guid.NewGuid():N}";
        await SeedOperationForBookingAsync(tenantId, bookingId);

        Assert.Empty(await ReadAsync($"/api/Bookings/{bookingId}/notifications", tenantId));
    }

    [Fact]
    public async Task AnOperationsRead_ShowsOnlyThatOperation()
    {
        var tenantId = $"t-notif-op-{Guid.NewGuid():N}";
        var pending = await SeedOperationForBookingAsync(tenantId, bookingId: null);
        var other = await SeedOperationForBookingAsync(tenantId, bookingId: null);
        var mine = await SeedNotificationAsync(tenantId, pending, NotificationEvent.RequestAccepted, NotificationState.Sending);
        await SeedNotificationAsync(tenantId, other, NotificationEvent.RequestAccepted, NotificationState.Delivered);

        var rows = await ReadAsync($"/api/booking-operations/{pending}/notifications", tenantId);

        var row = Assert.Single(rows);
        Assert.Equal(mine, row.GetProperty("notification_id").GetString());
        Assert.Equal("sending", row.GetProperty("state").GetString());
    }

    /// <summary>Inv-13: the same booking id under another tenant reveals nothing.</summary>
    [Fact]
    public async Task AnotherTenant_ReadingTheSameBookingId_SeesNothing()
    {
        var owner = $"t-notif-a-{Guid.NewGuid():N}";
        var intruder = $"t-notif-b-{Guid.NewGuid():N}";
        var bookingId = $"bk-{Guid.NewGuid():N}";
        var op = await SeedOperationForBookingAsync(owner, bookingId);
        await SeedNotificationAsync(owner, op, NotificationEvent.BookingConfirmed, NotificationState.Delivered);

        Assert.Empty(await ReadAsync($"/api/Bookings/{bookingId}/notifications", intruder));
        Assert.Empty(await ReadAsync($"/api/booking-operations/{op}/notifications", intruder));
    }

    /// <summary>
    /// An operator identity is not a delivery provider. Receipts are accepted only with the
    /// webhook secret, so the console — which never holds it — cannot assert one.
    /// </summary>
    [Fact]
    public async Task AnOperatorIdentity_CannotPostADeliveryReceipt()
    {
        var tenantId = $"t-notif-post-{Guid.NewGuid():N}";
        var op = await SeedOperationForBookingAsync(tenantId, bookingId: null);
        var notificationId = await SeedNotificationAsync(
            tenantId, op, NotificationEvent.BookingConfirmed, NotificationState.Accepted);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(JsonSerializer.Serialize(new
            {
                notification_id = notificationId,
                provider_message_id = "pmsg_forged",
                status = "delivered",
            })).ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.WithRequestHeader("X-Reason-Code", "guest_said_they_got_it");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.Notifications.AsNoTracking().SingleAsync(n => n.NotificationId == notificationId);
        Assert.Equal(NotificationState.Accepted, row.State);
    }
}
