#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Reporting;

/// <summary>
/// Integration tests proving the §11 product-reporting separation invariants (spec 13 §5.3, §11, §26).
///
/// Each test uses a unique tenant id to ensure full isolation.
/// Seeds rows directly via <see cref="Acceptance.AcceptanceApiFixture.CreateContext"/> to decouple
/// funnel assertions from the booking-submission HTTP flow.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class BookingFunnelTests(Acceptance.AcceptanceApiFixture fixture)
{
    // ---------------------------------------------------------------------------
    // Helper: seed a minimal BookingIntent + BookingIntentRevision + BookingOperation.
    // Returns the operationId.
    // ---------------------------------------------------------------------------

    private static async Task<string> SeedOperationAsync(
        Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx,
        string tenantId,
        OperationStatus status,
        DateTimeOffset? createdAt = null)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var now = createdAt ?? DateTimeOffset.UtcNow;

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
            CanonicalPayload = "{}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await ctx.SaveChangesAsync();
        return operationId;
    }

    /// <summary>Seeds a Notification row with the given State linked to an operation.</summary>
    private static async Task SeedNotificationAsync(
        Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx,
        string tenantId,
        string operationId,
        NotificationState state)
    {
        ctx.Notifications.Add(new Notification
        {
            TenantId = tenantId,
            NotificationId = $"notif-{Guid.NewGuid():N}",
            OperationId = operationId,
            EventType = NotificationEvent.BookingConfirmed,
            RecipientRef = "test@example.com",
            TemplateVersion = "v1",
            MaterialRevision = 1,
            State = state,
            Channel = NotificationChannel.Email,
            Priority = MessagePriority.High,
            AttemptCount = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------
    // T1: accepted-but-unconfirmed → accepted=1, confirmed=0, notified=0
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AcceptedButUnconfirmed_FunnelReportsAccepted1Confirmed0Notified0()
    {
        var tenantId = $"funnel-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // Seed one operation in a non-Booked status (e.g. Accepted).
        await using var ctx = fixture.CreateContext(tenantId);
        await SeedOperationAsync(ctx, tenantId, OperationStatus.Accepted, createdAt: now);

        var from = now.AddMinutes(-1);
        var to = now.AddMinutes(1);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/reporting/funnel?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("accepted").GetInt32());
        Assert.Equal(0, root.GetProperty("confirmed").GetInt32());
        Assert.Equal(0, root.GetProperty("notified").GetInt32());
    }

    // ---------------------------------------------------------------------------
    // T2: confirmed-but-unnotified → confirmed=1, notified=0 (funnels do not collapse)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmedButUnnotified_FunnelReportsConfirmed1Notified0()
    {
        var tenantId = $"funnel-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        // Seed a Booked operation with NO notification row.
        await SeedOperationAsync(ctx, tenantId, OperationStatus.Booked, createdAt: now);

        var from = now.AddMinutes(-1);
        var to = now.AddMinutes(1);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/reporting/funnel?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        // accepted=1 (every durable operation counts)
        Assert.Equal(1, root.GetProperty("accepted").GetInt32());
        // confirmed=1 (Booked)
        Assert.Equal(1, root.GetProperty("confirmed").GetInt32());
        // notified=0 (no Delivered notification) — proves confirmed ≠ notified
        Assert.Equal(0, root.GetProperty("notified").GetInt32());
        // notifyPending = confirmed − notified = 1
        Assert.Equal(1, root.GetProperty("notifyPending").GetInt32());
    }

    // ---------------------------------------------------------------------------
    // T3: ambiguous (Reconciling) → confirmed=0, ambiguous=1;
    //     flip to Booked → confirmed=1, ambiguous=0 (Inv-7)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AmbiguousResolves_ConfirmedAdvancesWhenBooked()
    {
        var tenantId = $"funnel-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await SeedOperationAsync(ctx, tenantId, OperationStatus.Reconciling, createdAt: now);

        var from = now.AddMinutes(-1);
        var to = now.AddMinutes(1);

        // Phase 1: Reconciling → ambiguous=1, confirmed=0
        var result1 = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/reporting/funnel?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc1 = JsonDocument.Parse(await result1.ReadAsTextAsync());
        var root1 = doc1.RootElement;

        Assert.Equal(0, root1.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, root1.GetProperty("ambiguous").GetInt32());

        // Phase 2: directly update the operation to Booked in the DB.
        await ctx.BookingOperations
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.OperationId == operationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, OperationStatus.Booked)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        var result2 = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/reporting/funnel?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc2 = JsonDocument.Parse(await result2.ReadAsTextAsync());
        var root2 = doc2.RootElement;

        // After reconciliation resolves: confirmed=1, ambiguous=0
        Assert.Equal(1, root2.GetProperty("confirmed").GetInt32());
        Assert.Equal(0, root2.GetProperty("ambiguous").GetInt32());
    }

    // ---------------------------------------------------------------------------
    // T4: Booked + Delivered notification → notified=1
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task BookedWithDeliveredNotification_FunnelReportsNotified1()
    {
        var tenantId = $"funnel-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await SeedOperationAsync(ctx, tenantId, OperationStatus.Booked, createdAt: now);
        await SeedNotificationAsync(ctx, tenantId, operationId, NotificationState.Delivered);

        var from = now.AddMinutes(-1);
        var to = now.AddMinutes(1);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/reporting/funnel?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("accepted").GetInt32());
        Assert.Equal(1, root.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, root.GetProperty("notified").GetInt32());
        // notifyPending = confirmed − notified = 0
        Assert.Equal(0, root.GetProperty("notifyPending").GetInt32());
    }

    // ---------------------------------------------------------------------------
    // T5: missing tenant header → 400
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetFunnel_MissingTenantHeader_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/reporting/funnel");
            // Intentionally omit X-Tenant-Id header.
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------------
    // T6: default window (no from/to) → 200 with valid funnel
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetFunnel_NoWindowParams_Returns200WithDefaultWindow()
    {
        var tenantId = $"funnel-{Guid.NewGuid():N}";

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/reporting/funnel");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = doc.RootElement;

        // Empty tenant — all counts must be zero.
        Assert.Equal(0, root.GetProperty("accepted").GetInt32());
        Assert.Equal(0, root.GetProperty("confirmed").GetInt32());
        Assert.Equal(0, root.GetProperty("notified").GetInt32());
        // from and to must be present and valid.
        Assert.True(root.TryGetProperty("from", out _));
        Assert.True(root.TryGetProperty("to", out _));
    }
}
