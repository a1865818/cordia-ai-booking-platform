#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Integration tests for POST /api/notification-webhooks/delivery-receipts (spec 10 §6.1, T3).
///
/// Each test seeds its own tenant + parent BookingOperation + notifications row (state=Accepted)
/// directly via DbContext, then calls the endpoint via the in-process Alba host.
///
/// Tests:
///   T1  Delivered receipt → 202; DB row now delivered.
///   T2  Bounced receipt   → 202; DB row now failed_permanent, failure_category=bounce.
///   T3  Unknown notificationId → 404.
///   T4  Wrong/missing X-Webhook-Secret → 401.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class NotificationWebhookTests(AcceptanceApiFixture fixture)
{
    private const string WebhookSecret = "test-webhook-secret-t3";

    // Build an Alba host that overrides the webhook secret so tests can authenticate.
    // We reuse the shared fixture host but override the secret via UseSetting.
    // Because the fixture host is shared, we build a dedicated short-lived host per test class.
    private static Task<IAlbaHost> BuildHostAsync()
        => AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            builder.UseSetting("Notifications:WebhookSecret", WebhookSecret);
        });

    private static async Task<string> SeedNotificationAsync(
        AcceptanceApiFixture fixture,
        string tenantId,
        string notificationId,
        NotificationState state = NotificationState.Accepted,
        string? providerRef = "pmsg_test",
        long version = 1)
    {
        var ctx = fixture.CreateContext(tenantId);
        await using (ctx)
        {
            // Parent operation required (FK constraint).
            var operationId = await TestData.SeedOperationAsync(ctx, tenantId);

            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notificationId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip_test",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = state,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderRef = providerRef,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = version,
            });
            await ctx.SaveChangesAsync();
        }

        return notificationId;
    }

    private static string BuildBody(string notificationId, string providerMessageId, string status)
    {
        var obj = new
        {
            notification_id = notificationId,
            provider_message_id = providerMessageId,
            status,
        };
        return JsonSerializer.Serialize(obj);
    }

    // ── T1: Delivered receipt → 202; row updated to Delivered ─────────────────────────────────────

    [Fact]
    public async Task Post_DeliveredReceipt_Returns202_RowBecomesDelivered()
    {
        var tenantId = $"wh-t1-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await SeedNotificationAsync(fixture, tenantId, notificationId,
            state: NotificationState.Accepted, providerRef: "pmsg_test");

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert DB row is now Delivered.
        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.Notifications.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.NotificationId == notificationId);

        Assert.NotNull(row);
        Assert.Equal(NotificationState.Delivered, row.State);
    }

    // ── T2: Bounced receipt → 202; row updated to FailedPermanent + failure_category=bounce ───────

    [Fact]
    public async Task Post_BouncedReceipt_Returns202_RowBecomesFailedPermanentWithBounce()
    {
        var tenantId = $"wh-t2-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await SeedNotificationAsync(fixture, tenantId, notificationId,
            state: NotificationState.Accepted, providerRef: "pmsg_test");

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "bounced"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert DB row is now FailedPermanent with failure_category=bounce.
        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.Notifications.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.NotificationId == notificationId);

        Assert.NotNull(row);
        Assert.Equal(NotificationState.FailedPermanent, row.State);
        Assert.Equal("bounce", row.FailureCategory);
    }

    // ── T3: Unknown notificationId → 404 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownNotificationId_Returns404()
    {
        var tenantId = $"wh-t3-{Guid.NewGuid():N}";
        var unknownNotificationId = $"notif-does-not-exist-{Guid.NewGuid():N}";

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(unknownNotificationId, "pmsg_any", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ── T4: Wrong/missing X-Webhook-Secret → 401 ──────────────────────────────────────────────────

    [Fact]
    public async Task Post_WrongWebhookSecret_Returns401()
    {
        var tenantId = $"wh-t4-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await SeedNotificationAsync(fixture, tenantId, notificationId,
            state: NotificationState.Accepted, providerRef: "pmsg_test");

        await using var host = await BuildHostAsync();

        // Wrong secret.
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", "wrong-secret");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        // Missing secret.
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            // Intentionally omit X-Webhook-Secret.
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── T5 (carry-forward): Missing X-Tenant-Id header → 400 ─────────────────────────────────────

    [Fact]
    public async Task Post_MissingTenantIdHeader_Returns400()
    {
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_any", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            // X-Tenant-Id intentionally omitted; X-Webhook-Secret provided and valid.
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── T6 (carry-forward): Unknown status string → 400 ──────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownStatus_Returns400()
    {
        var tenantId = $"wh-t6-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await SeedNotificationAsync(fixture, tenantId, notificationId,
            state: NotificationState.Accepted, providerRef: "pmsg_test");

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "garbage"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── T7 (carry-forward): Configured secret present but X-Webhook-Secret header omitted → 401 ──

    [Fact]
    public async Task Post_OmittedWebhookSecretHeader_Returns401()
    {
        var tenantId = $"wh-t7-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";

        await SeedNotificationAsync(fixture, tenantId, notificationId,
            state: NotificationState.Accepted, providerRef: "pmsg_test");

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered"))
                .ToUrl("/api/notification-webhooks/delivery-receipts");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            // X-Webhook-Secret header intentionally omitted (distinct from wrong-secret scenario).
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }
}
