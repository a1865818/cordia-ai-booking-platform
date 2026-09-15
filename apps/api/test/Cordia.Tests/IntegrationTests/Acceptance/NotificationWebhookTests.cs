#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using Cordia.Api.Security;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Integration tests for POST /api/notification-webhooks/delivery-receipts (spec 10 §6.1, T3; #74).
///
/// Each test seeds its own tenant + parent BookingOperation + notifications row (state=Accepted)
/// directly via DbContext, then calls the endpoint via an in-process Alba host.
///
/// <para><b>Authentication is an HMAC signature now (#74).</b> T1–T7 were written against a plain
/// <c>X-Webhook-Secret</c> header; they are rewritten to sign each request with
/// <see cref="WebhookSignature"/> — timestamp, tenant and raw body — exactly as a provider must.
/// T8/T9 keep their subject (which secret source keys the signature, and which wins), and T10–T15 pin
/// what the signature adds: a tampered body, a stale or future timestamp, a different tenant and the
/// retired header are all refused.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class NotificationWebhookTests(AcceptanceApiFixture fixture)
{
    private const string WebhookSecret = "test-webhook-secret-t3";
    private const string Url = "/api/notification-webhooks/delivery-receipts";

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
        => JsonSerializer.Serialize(new
        {
            notification_id = notificationId,
            provider_message_id = providerMessageId,
            status,
        });

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Posts <paramref name="body"/> signed the way a provider must sign it.</summary>
    private static Task<IScenarioResult> PostSignedAsync(
        IAlbaHost host,
        string body,
        string? tenantId,
        string secret,
        HttpStatusCode expected,
        string? timestamp = null,
        string? signedTenant = null,
        string? signedBody = null)
    {
        var ts = timestamp ?? Now();
        var signature = WebhookSignature.Compute(
            secret, ts, signedTenant ?? tenantId ?? string.Empty, Encoding.UTF8.GetBytes(signedBody ?? body));

        return host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl(Url);
            if (tenantId is not null)
                x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader(WebhookSignature.TimestampHeader, ts);
            x.WithRequestHeader(WebhookSignature.SignatureHeader, signature);
            x.StatusCodeShouldBe(expected);
        });
    }

    private async Task<Notification?> ReadAsync(string tenantId, string notificationId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        return await ctx.Notifications.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.NotificationId == notificationId);
    }

    // ── T1: Delivered receipt → 202; row updated to Delivered ─────────────────────────────────────

    [Fact]
    public async Task Post_DeliveredReceipt_Returns202_RowBecomesDelivered()
    {
        var tenantId = $"wh-t1-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, WebhookSecret, HttpStatusCode.Accepted);

        var row = await ReadAsync(tenantId, notificationId);
        Assert.NotNull(row);
        Assert.Equal(NotificationState.Delivered, row.State);
    }

    // ── T2: Bounced receipt → 202; row updated to FailedPermanent + failure_category=bounce ───────

    [Fact]
    public async Task Post_BouncedReceipt_Returns202_RowBecomesFailedPermanentWithBounce()
    {
        var tenantId = $"wh-t2-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "bounced"), tenantId, WebhookSecret, HttpStatusCode.Accepted);

        var row = await ReadAsync(tenantId, notificationId);
        Assert.NotNull(row);
        Assert.Equal(NotificationState.FailedPermanent, row.State);
        Assert.Equal("bounce", row.FailureCategory);
    }

    // ── T3: Unknown notificationId → 404 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownNotificationId_Returns404()
    {
        var tenantId = $"wh-t3-{Guid.NewGuid():N}";

        await using var host = await BuildHostAsync();
        await PostSignedAsync(
            host, BuildBody($"notif-does-not-exist-{Guid.NewGuid():N}", "pmsg_any", "delivered"),
            tenantId, WebhookSecret, HttpStatusCode.NotFound);
    }

    // ── T4: Signature keyed by the wrong secret → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task Post_SignedWithTheWrongSecret_Returns401()
    {
        var tenantId = $"wh-t4-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, "wrong-secret", HttpStatusCode.Unauthorized);

        Assert.Equal(NotificationState.Accepted, (await ReadAsync(tenantId, notificationId))!.State);
    }

    // ── T5: Missing X-Tenant-Id header, validly signed → 400 ─────────────────────────────────────

    [Fact]
    public async Task Post_MissingTenantIdHeader_Returns400()
    {
        await using var host = await BuildHostAsync();
        await PostSignedAsync(
            host, BuildBody($"notif-{Guid.NewGuid():N}", "pmsg_any", "delivered"),
            tenantId: null, WebhookSecret, HttpStatusCode.BadRequest);
    }

    // ── T6: Unknown status string, validly signed → 400 ──────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownStatus_Returns400()
    {
        var tenantId = $"wh-t6-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "garbage"), tenantId, WebhookSecret, HttpStatusCode.BadRequest);
    }

    // ── T7: No signature headers at all → 401 ────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithoutSignatureHeaders_Returns401()
    {
        var tenantId = $"wh-t7-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered")).ToUrl(Url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── T8 (spec 14 §24.4): the signature is keyed by the ISecretStore secret ─────────────────────

    /// <summary>
    /// Spec 14 §24.4: the endpoint authenticates against the ISecretStore secret
    /// (Secrets:NotificationsWebhookSecret) when only that source is configured.
    /// </summary>
    [Fact]
    public async Task Post_SecretFromSecretStore_Authenticates()
    {
        var tenantId = $"wh-t8-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        const string storeSecret = "store-webhook-secret-t8";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            builder.UseSetting("Secrets:NotificationsWebhookSecret", storeSecret);
        });

        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, storeSecret, HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Spec 14 §24.4 precedence: when BOTH sources are configured, the ISecretStore value keys the
    /// signature — one keyed by the legacy config value no longer authenticates.
    /// </summary>
    [Fact]
    public async Task Post_SecretStoreTakesPrecedenceOverLegacyConfig()
    {
        var tenantId = $"wh-t9-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        const string storeSecret = "store-webhook-secret-t9";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            builder.UseSetting("Secrets:NotificationsWebhookSecret", storeSecret);
            builder.UseSetting("Notifications:WebhookSecret", WebhookSecret);
        });

        var body = BuildBody(notificationId, "pmsg_test", "delivered");
        await PostSignedAsync(host, body, tenantId, WebhookSecret, HttpStatusCode.Unauthorized);
        await PostSignedAsync(host, body, tenantId, storeSecret, HttpStatusCode.Accepted);
    }

    // ── T10–T15 (#74): what the signature adds over a shared secret ──────────────────────────────

    /// <summary>A body altered after signing — here, "bounced" flipped to "delivered" — is refused.</summary>
    [Fact]
    public async Task Post_WithABodyAlteredAfterSigning_Returns401_AndChangesNothing()
    {
        var tenantId = $"wh-t10-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(
            host,
            body: BuildBody(notificationId, "pmsg_test", "delivered"),
            tenantId, WebhookSecret, HttpStatusCode.Unauthorized,
            signedBody: BuildBody(notificationId, "pmsg_test", "bounced"));

        Assert.Equal(NotificationState.Accepted, (await ReadAsync(tenantId, notificationId))!.State);
    }

    /// <summary>A captured request replayed after the window is refused, even though its signature is genuine.</summary>
    [Fact]
    public async Task Post_WithAStaleTimestamp_Returns401()
    {
        var tenantId = $"wh-t11-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        var stale = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, WebhookSecret,
            HttpStatusCode.Unauthorized, timestamp: stale);
    }

    [Fact]
    public async Task Post_WithATimestampTooFarInTheFuture_Returns401()
    {
        var tenantId = $"wh-t12-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        var future = DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, WebhookSecret,
            HttpStatusCode.Unauthorized, timestamp: future);
    }

    /// <summary>The paired negative for the window: a little clock skew is tolerated.</summary>
    [Fact]
    public async Task Post_WithATimestampInsideTheWindow_IsAccepted()
    {
        var tenantId = $"wh-t13-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        var skewed = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, WebhookSecret,
            HttpStatusCode.Accepted, timestamp: skewed);
    }

    /// <summary>The tenant arrives in a header, so it is signed: a receipt signed for one tenant cannot be replayed at another.</summary>
    [Fact]
    public async Task Post_SignedForADifferentTenant_Returns401()
    {
        var tenantId = $"wh-t14-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await PostSignedAsync(host, BuildBody(notificationId, "pmsg_test", "delivered"), tenantId, WebhookSecret,
            HttpStatusCode.Unauthorized, signedTenant: $"wh-other-{Guid.NewGuid():N}");
    }

    /// <summary>The retired shared-secret header, even carrying the right secret, authenticates nothing.</summary>
    [Fact]
    public async Task Post_WithOnlyTheRetiredSecretHeader_Returns401()
    {
        var tenantId = $"wh-t15-{Guid.NewGuid():N}";
        var notificationId = $"notif-{Guid.NewGuid():N}";
        await SeedNotificationAsync(fixture, tenantId, notificationId);

        await using var host = await BuildHostAsync();
        await host.Scenario(x =>
        {
            x.Post.RawJson(BuildBody(notificationId, "pmsg_test", "delivered")).ToUrl(Url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Webhook-Secret", WebhookSecret);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        Assert.Equal(NotificationState.Accepted, (await ReadAsync(tenantId, notificationId))!.State);
    }
}
