#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for the menu-ingestion consumer (spec 11 §5.2) end-to-end through the outbox
/// publisher → transport → inbox → handler, against real PostgreSQL. Asserts the catalog reaches
/// DraftReady (with draft items) on a clean artifact and Quarantined on a malware hit, with the
/// consumer_inbox row admitted for "menu.processor" (Inv-5).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class MenuIngestionTests(PostgresFixture fixture)
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
                // The real extractor is now the DI default, so this suite must opt into the stub
                // explicitly or it would point at localhost:2024 (issue #87 D-E).
                ["LangGraph:UseStubExtractor"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    /// <summary>Container whose isolated extractor always throws — simulates an extraction failure.</summary>
    private static ServiceProvider BuildContainerWithFailingExtractor()
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
                // The real extractor is now the DI default, so this suite must opt into the stub
                // explicitly or it would point at localhost:2024 (issue #87 D-E).
                ["LangGraph:UseStubExtractor"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        services.RemoveAll<IIsolatedMenuExtractor>();
        services.AddSingleton<IIsolatedMenuExtractor, ThrowingMenuExtractor>();
        return services.BuildServiceProvider();
    }

    private sealed class ThrowingMenuExtractor : IIsolatedMenuExtractor
    {
        public Task<MenuExtractionResult> ExtractAsync(string artifactRef, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated extraction failure");
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    private async Task NeutralizeExistingUnpublishedAsync()
    {
        await using var ctx = fixture.CreateContext("t11-menu-cleanup");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    private async Task<(string tenantId, string catalogId, string messageId)> SeedUploadedCatalogAndIngestMessageAsync(
        string sourceArtifactRef)
    {
        var tenantId = TestIds.New("t11-menu");
        var catalogId = TestIds.New("cat");
        var messageId = TestIds.New("msg");

        await using var seedCtx = fixture.CreateContext(tenantId);
        seedCtx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = catalogId,
            Status = CatalogStatus.Uploaded,
            SourceHash = "sha256-test",
        });
        seedCtx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            MessageType = "menu.commands.ingest.v1",
            CorrelationId = catalogId,
            Payload = $"{{\"catalog_id\":\"{catalogId}\",\"source_artifact_ref\":\"{sourceArtifactRef}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await seedCtx.SaveChangesAsync();

        return (tenantId, catalogId, messageId);
    }

    [Fact]
    public async Task CleanArtifact_DriveToDraftReady_WithItems_AndAdmittedInbox()
    {
        await NeutralizeExistingUnpublishedAsync();
        var (tenantId, catalogId, messageId) = await SeedUploadedCatalogAndIngestMessageAsync("s3://tenant/menu.pdf");

        await using var sp = BuildContainer();
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.DraftReady, catalog.Status);

        var items = await assertCtx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.CatalogId == catalogId)
            .ToListAsync();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.StartsWith($"{catalogId}:", i.ItemId));

        var inbox = await assertCtx.ConsumerInbox.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.ConsumerName == "menu.processor" && i.MessageId == messageId);
        Assert.NotNull(inbox);
    }

    [Fact]
    public async Task DuplicateDelivery_IsIdempotent_NoOp()
    {
        await NeutralizeExistingUnpublishedAsync();
        var (tenantId, catalogId, messageId) = await SeedUploadedCatalogAndIngestMessageAsync("s3://tenant/menu.pdf");

        await using var sp = BuildContainer();

        // First delivery drives Uploaded → DraftReady and admits the consumer_inbox row.
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        int itemsAfterFirst;
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var c1 = await ctx1.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
            Assert.Equal(CatalogStatus.DraftReady, c1.Status);
            itemsAfterFirst = await ctx1.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
                .CountAsync(i => i.TenantId == tenantId && i.CatalogId == catalogId);
            Assert.True(itemsAfterFirst > 0);

            // Simulate redelivery: re-arm the SAME outbox row (same message_id) for a second publish.
            await ctx1.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.MessageId == messageId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(m => m.PublishedAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LeaseOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
        }

        // Second delivery: InboxDeduplicationBehavior sees the existing inbox row → no-op.
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // Catalog is unchanged (not re-processed) and still DraftReady.
        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.DraftReady, catalog.Status);

        // No duplicate items were inserted on redelivery.
        var itemsAfterSecond = await assertCtx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == tenantId && i.CatalogId == catalogId);
        Assert.Equal(itemsAfterFirst, itemsAfterSecond);

        // Exactly one consumer_inbox row for this consumer/message (admit-once, Inv-5).
        var inboxCount = await assertCtx.ConsumerInbox.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == tenantId && i.ConsumerName == "menu.processor" && i.MessageId == messageId);
        Assert.Equal(1, inboxCount);
    }

    [Fact]
    public async Task MalwareArtifact_Quarantines_NoItems_AuditWritten()
    {
        await NeutralizeExistingUnpublishedAsync();
        var (tenantId, catalogId, _) = await SeedUploadedCatalogAndIngestMessageAsync("s3://tenant/malware.pdf");

        await using var sp = BuildContainer();
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.Quarantined, catalog.Status);

        var itemCount = await assertCtx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == tenantId && i.CatalogId == catalogId);
        Assert.Equal(0, itemCount);

        var audit = await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EventType == "catalog.quarantined" && e.SubjectRef == catalogId);
        Assert.NotNull(audit);

        // The owner is alerted (Inv-14): a quarantine event is enqueued on the outbox in the same inbox txn.
        var alert = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.MessageType == "menu.catalog.quarantined.v1" && m.CorrelationId == catalogId);
        Assert.NotNull(alert);
    }

    [Fact]
    public async Task ExtractionFailure_SettlesExtractFailed_AuditAndOwnerAlert_NoItems()
    {
        await NeutralizeExistingUnpublishedAsync();
        // Clean artifact (scan passes → Extracting), but the extractor throws.
        var (tenantId, catalogId, messageId) = await SeedUploadedCatalogAndIngestMessageAsync("s3://tenant/menu.pdf");

        await using var sp = BuildContainerWithFailingExtractor();
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // Settled terminally AT THE POINT OF FAILURE (D4 amendment, owner ruling 2026-08-06) —
        // not left in Extracting for MenuIngestionMonitor to infer from a staleness threshold.
        // The handler already knew the extraction had failed; reporting "being read" for another
        // fifteen minutes was a confident falsehood about a fact in hand. Never DraftReady or
        // Published.
        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.ExtractFailed, catalog.Status);

        var itemCount = await assertCtx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == tenantId && i.CatalogId == catalogId);
        Assert.Equal(0, itemCount);

        var audit = await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EventType == "catalog.extraction_failed" && e.SubjectRef == catalogId);
        Assert.NotNull(audit);

        // The owner is alerted (Inv-14): an extraction-failed event is enqueued on the outbox.
        var alert = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                && m.MessageType == "menu.catalog.extraction_failed.v1" && m.CorrelationId == catalogId);
        Assert.NotNull(alert);

        // Fail-secure: the handler did not throw, so the message was consumed (inbox row admitted), not poison-looped.
        var inbox = await assertCtx.ConsumerInbox.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.ConsumerName == "menu.processor" && i.MessageId == messageId);
        Assert.NotNull(inbox);
    }
}
