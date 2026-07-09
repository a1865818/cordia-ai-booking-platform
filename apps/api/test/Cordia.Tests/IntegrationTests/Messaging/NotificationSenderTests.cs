#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for the SendNotification consumer (spec 10 §4, §6.2, T2).
/// Mirrors <see cref="RejectionTests"/> harness: seed outbox, neutralize pre-existing
/// unpublished rows, tick the publisher, assert notifications + consumer_inbox rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class NotificationSenderTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    private async Task NeutralizeExistingUnpublishedAsync(string cleanupTenant = "t10-ntf-cleanup")
    {
        await using var ctx = fixture.CreateContext(cleanupTenant);
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    /// <summary>
    /// Happy path: booking_confirmed outbox row → publisher tick → exactly ONE notifications row
    /// in state accepted with a non-null provider_ref; consumer_inbox admitted for notification.sender.
    /// </summary>
    [Fact]
    public async Task BookingConfirmed_OutboxRow_YieldsOneAcceptedNotification_AndAdmittedInbox()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t10-ntf-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: exactly ONE notifications row for this operation, state=Accepted ────────────
        var notifications = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.OperationId == operationId)
            .ToListAsync();

        Assert.Single(notifications);
        var notif = notifications[0];
        Assert.Equal(NotificationState.Accepted, notif.State);
        Assert.Equal(NotificationEvent.BookingConfirmed, notif.EventType);
        Assert.Equal(MessagePriority.High, notif.Priority);
        Assert.NotNull(notif.ProviderRef);

        // ── Assert: consumer_inbox admitted for "notification.sender" ─────────────────────────
        var inbox = await assertCtx.ConsumerInbox
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i =>
                i.TenantId == tenantId &&
                i.ConsumerName == "notification.sender" &&
                i.MessageId == messageId);

        Assert.NotNull(inbox);
    }

    /// <summary>
    /// Spec 11 §5.1 / review 2026-07-03 C1: a waitlist offer notification is emitted BEFORE any
    /// booking_operations row exists (the op_wl_* operation is minted only at accept). The send
    /// must survive the real outbox → transport → sender → INSERT path: the notifications row is
    /// created with a NULL operation_id (composite FK skipped), state Accepted, and the outbox row
    /// is published — never FK-rejected (PG 23503) or dead-lettered.
    /// </summary>
    [Fact]
    public async Task WaitlistOffer_NoOperationRow_DeliversNotification_WithNullOperationId()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t11-wlntf-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op_wl_{Guid.NewGuid():N}"; // deliberately NO booking_operations row

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "waitlist.offer.notified.v1",
                CorrelationId = $"wle-{Guid.NewGuid():N}",
                OperationId = operationId,
                Payload = $"{{\"offer_id\":\"wlo-1\",\"offer_expires_at\":\"2026-07-03T10:00:00Z\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var notifications = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId)
            .ToListAsync();

        Assert.Single(notifications);
        var notif = notifications[0];
        Assert.Equal(NotificationEvent.WaitlistOffer, notif.EventType);
        Assert.Equal(MessagePriority.Critical, notif.Priority);
        Assert.Equal(NotificationState.Accepted, notif.State);
        Assert.Null(notif.OperationId); // FK skipped — no booking_operations row exists yet

        // The outbox row was published (not left unpublished, not dead-lettered).
        var outboxRow = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(m => m.TenantId == tenantId && m.MessageId == messageId);
        Assert.NotNull(outboxRow.PublishedAt);
        Assert.False(await assertCtx.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tenantId && d.MessageId == messageId));
    }

    /// <summary>
    /// Same end-to-end path for the OFFER-EXPIRED notification: this event previously crashed with
    /// KeyNotFoundException because NotificationIdentity.EventLabels had no WaitlistOfferExpired
    /// entry — invisible to mocked-repo unit tests, fatal on the real send (poison loop → DLQ).
    /// </summary>
    [Fact]
    public async Task WaitlistOfferExpired_NoOperationRow_DeliversNotification()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t11-wlexp-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op_wl_{Guid.NewGuid():N}"; // no booking_operations row

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "waitlist.offer.expired.v1",
                CorrelationId = $"wle-{Guid.NewGuid():N}",
                OperationId = operationId,
                Payload = $"{{\"offer_id\":\"wlo-1\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var notif = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(n => n.TenantId == tenantId);
        Assert.Equal(NotificationEvent.WaitlistOfferExpired, notif.EventType);
        Assert.Equal(NotificationState.Accepted, notif.State);
        Assert.Null(notif.OperationId);
        Assert.False(await assertCtx.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tenantId && d.MessageId == messageId));
    }

    /// <summary>
    /// Tenant isolation (§27.6): a notification row created under tenant A is NOT visible
    /// when queried under tenant B — proving the global query filter is enforced (spec 10 §27.6, T6).
    /// </summary>
    [Fact]
    public async Task TenantIsolation_NotificationUnderTenantA_NotVisibleToTenantB()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantA = $"t10-iso-a-{Guid.NewGuid():N}";
        var tenantB = $"t10-iso-b-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantA))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantA, OperationStatus.Booked);

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantA,
                MessageId = messageId,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantA}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert A: tenant A's notification exists ──────────────────────────────────────────────
        await using var ctxA = fixture.CreateContext(tenantA);
        var countA = await ctxA.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(n => n.TenantId == tenantA && n.OperationId == operationId);
        Assert.Equal(1, countA);

        // ── Assert B: the same notification is NOT visible under tenant B ─────────────────────────
        // Query via the normal (non-IgnoreQueryFilters) path that the global filter enforces, and
        // also via the repository interface's GetAsync which is tenant-scoped.
        await using var ctxB = fixture.CreateContext(tenantB);

        // Direct DbSet query scoped to tenantB — global filter would limit to tenantB rows only.
        var rowB = await ctxB.Notifications
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.OperationId == operationId);
        Assert.Null(rowB);

        // Cross-tenant lookup using the raw notification_id derived by the consumer;
        // we recover it from the tenantA row so the query is deterministic.
        var notifA = await ctxA.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.TenantId == tenantA && n.OperationId == operationId);
        Assert.NotNull(notifA);

        // GetAsync scoped to tenantB with tenantA's notification_id must return null.
        var notificationRepo = sp.GetRequiredService<INotificationRepository>();
        var crossTenantResult = await notificationRepo.GetAsync(tenantB, notifA!.NotificationId);
        Assert.Null(crossTenantResult);
    }

    /// <summary>
    /// Logical dedup (Inv-9): two distinct outbox messages for the same operation and event type
    /// produce only ONE notifications row; the second is suppressed and audited as
    /// "notification.suppressed_duplicate".
    /// </summary>
    [Fact]
    public async Task LogicalDedup_TwoOutboxRowsSameIdentity_YieldsOneNotification_SecondSuppressed()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t10-dedup-{Guid.NewGuid():N}";
        var msgId1 = $"msg-{Guid.NewGuid():N}";
        var msgId2 = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // Two distinct messages for the same operation + event type.
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgId1,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgId2,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: exactly ONE notifications row (dedup) ────────────────────────────────────────
        var count = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(n => n.TenantId == tenantId && n.OperationId == operationId);

        Assert.Equal(1, count);

        // ── Assert: audit for suppressed_duplicate exists ────────────────────────────────────────
        var suppressedAudit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "notification.suppressed_duplicate");

        Assert.NotNull(suppressedAudit);
    }

    /// <summary>
    /// Material-revision change (Inv-10 / FM#10 / §27.2): the SAME operation+event, delivered before
    /// and after an intent-revision bump, derives a NEW notification_id and produces a SECOND distinct
    /// accepted notification row — while the prior (stale) row is left untouched. The material revision
    /// is an identity input, so a revision change is a *new* logical notification, not a dedup.
    /// </summary>
    [Fact]
    public async Task MaterialRevisionChange_YieldsSecondDistinctNotification_StaleUntouched()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t10-rev-{Guid.NewGuid():N}";
        var msgId1 = $"msg-{Guid.NewGuid():N}";
        var msgId2 = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // Pin the intent revision to 1, then enqueue the first confirmation trigger.
            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.IntentRevision, 1));

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgId1,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);

        // Tick 1: delivers the revision-1 confirmation → notification N1 (material_revision=1).
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // A material intent change occurs (e.g. modify-booking), bumping the revision to 2,
        // then a fresh confirmation trigger is emitted for the same operation+event.
        await using (var bumpCtx = fixture.CreateContext(tenantId))
        {
            var op = await bumpCtx.BookingOperations
                .IgnoreQueryFilters()
                .FirstAsync(x => x.TenantId == tenantId && x.OperationId == operationId);

            // Seed intent revision 2 so the FK (tenant_id, intent_id, intent_revision) is satisfied.
            bumpCtx.BookingIntentRevisions.Add(new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = op.IntentId,
                Revision = 2,
                CanonicalPayload = "{}",
                PayloadHash = "h2",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
            });
            op.IntentRevision = 2;

            bumpCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgId2,
                MessageType = "notification.booking_confirmed.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await bumpCtx.SaveChangesAsync();
        }

        // Tick 2: delivers the revision-2 confirmation → a NEW notification N2 (material_revision=2).
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);
        var notifications = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.OperationId == operationId)
            .OrderBy(n => n.MaterialRevision)
            .ToListAsync();

        // TWO distinct notifications: one per material revision, both accepted, distinct ids.
        Assert.Equal(2, notifications.Count);
        Assert.Equal(1, notifications[0].MaterialRevision);
        Assert.Equal(2, notifications[1].MaterialRevision);
        Assert.NotEqual(notifications[0].NotificationId, notifications[1].NotificationId);
        Assert.All(notifications, n => Assert.Equal(NotificationState.Accepted, n.State));
    }
}
