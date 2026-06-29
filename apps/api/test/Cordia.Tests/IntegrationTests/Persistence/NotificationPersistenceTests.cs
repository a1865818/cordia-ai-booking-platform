#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class NotificationPersistenceTests(PostgresFixture fixture)
{
    // (1) Insert notification; GetAsync round-trips EventType + State enums.
    [Fact]
    public async Task Insert_And_GetAsync_RoundTripsEnums()
    {
        var tenantId = $"t4-notif-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var notifId = $"notif-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-1",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Pending,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var notif = await readCtx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notifId);

        Assert.Equal(NotificationEvent.BookingConfirmed, notif.EventType);
        Assert.Equal(NotificationState.Pending, notif.State);
    }

    // (2) Dedup: insert duplicate (tenant_id, notification_id) → unique_violation 23505 (Inv-9).
    [Fact]
    public async Task Insert_DuplicateNotificationId_ThrowsUniqueViolation()
    {
        var tenantId = $"t4-notif-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var notifId = $"notif-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.RequestAccepted,
                RecipientRef = "recip-2",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Pending,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.Notifications.Add(new Notification
        {
            TenantId = tenantId,
            NotificationId = notifId, // same id → unique violation
            OperationId = operationId,
            EventType = NotificationEvent.RequestAccepted,
            RecipientRef = "recip-2",
            TemplateVersion = "v1",
            MaterialRevision = 1,
            State = NotificationState.Pending,
            Version = 0,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }

    // (3) Version-token: insert v0, two contexts TryTransitionStateAsync(expected 0, Sending)
    //     → exactly one wins, reload State=Sending Version=1.
    [Fact]
    public async Task TryTransitionStateAsync_ExactlyOneWins()
    {
        var tenantId = $"t4-notif-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var notifId = $"notif-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.WaitlistOffer,
                RecipientRef = "recip-3",
                TemplateVersion = "v1",
                MaterialRevision = 2,
                State = NotificationState.Pending,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<Notification>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.NotificationId == notifId,
            s => s.SetProperty(x => x.State, NotificationState.Sending));

        var rows2 = await VersionTokenUpdate.UpdateAsync<Notification>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.NotificationId == notifId,
            s => s.SetProperty(x => x.State, NotificationState.Sending));

        Assert.Equal(1, rows1 + rows2);
        Assert.True(VersionTokenUpdate.ShouldReload(rows1) || VersionTokenUpdate.ShouldReload(rows2));

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notifId);

        Assert.Equal(NotificationState.Sending, reloaded.State);
        Assert.Equal(1L, reloaded.Version);
    }

    // (4) Round-trip all new delivery columns: Channel, Priority, NextEligibleAt,
    //     AttemptCount, CreatedAt, UpdatedAt, FailureCategory.
    [Fact]
    public async Task Insert_And_GetAsync_RoundTripsDeliveryColumns()
    {
        var tenantId = $"t10-delivery-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var notifId = $"notif-{Guid.NewGuid():N}";
        var nextEligible = DateTimeOffset.UtcNow.AddMinutes(5);
        var createdAt = DateTimeOffset.UtcNow;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-4",
                TemplateVersion = "v2",
                MaterialRevision = 3,
                State = NotificationState.Pending,
                Channel = NotificationChannel.Email,
                Priority = MessagePriority.High,
                AttemptCount = 2,
                NextEligibleAt = nextEligible,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                FailureCategory = "provider_timeout",
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var notif = await readCtx.Notifications.AsNoTracking()
            .SingleAsync(n => n.NotificationId == notifId);

        Assert.Equal(NotificationChannel.Email, notif.Channel);
        Assert.Equal(MessagePriority.High, notif.Priority);
        Assert.Equal(2, notif.AttemptCount);
        Assert.Equal("provider_timeout", notif.FailureCategory);
        Assert.NotNull(notif.NextEligibleAt);
        // Postgres truncates to microseconds — allow a 1 ms tolerance
        Assert.InRange(notif.NextEligibleAt!.Value, nextEligible.AddMilliseconds(-1), nextEligible.AddMilliseconds(1));
        Assert.InRange(notif.CreatedAt, createdAt.AddMilliseconds(-1), createdAt.AddMilliseconds(1));
    }

    // (5) Dedup uses tenant scope — same notification_id under a different tenant_id is NOT a violation.
    [Fact]
    public async Task Insert_SameNotificationIdDifferentTenant_Succeeds()
    {
        var tenantA = $"t10-ta-{Guid.NewGuid():N}";
        var tenantB = $"t10-tb-{Guid.NewGuid():N}";

        string opA, opB;
        await using (var ctx = fixture.CreateContext(tenantA))
            opA = await TestData.SeedOperationAsync(ctx, tenantA);
        await using (var ctx = fixture.CreateContext(tenantB))
            opB = await TestData.SeedOperationAsync(ctx, tenantB);

        var sharedNotifId = $"ntf-shared-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantA))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantA,
                NotificationId = sharedNotifId,
                OperationId = opA,
                EventType = NotificationEvent.RequestAccepted,
                RecipientRef = "r",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Pending,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.Normal,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Different tenant — should NOT throw
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            ctx.Notifications.Add(new Notification
            {
                TenantId = tenantB,
                NotificationId = sharedNotifId,
                OperationId = opB,
                EventType = NotificationEvent.RequestAccepted,
                RecipientRef = "r",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Pending,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.Normal,
                Version = 0,
            });
            await ctx.SaveChangesAsync(); // must not throw
        }
    }
}
