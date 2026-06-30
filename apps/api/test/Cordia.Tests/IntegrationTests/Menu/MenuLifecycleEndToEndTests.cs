#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.Menu.ApproveCatalog;
using Cordia.Application.Handlers.Menu.PublishCatalog;
using Cordia.Application.Handlers.Menu.ReviewCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Menu;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Menu;

/// <summary>
/// The spec-11 §5.2 flagship suite: full menu lifecycle against real PostgreSQL —
/// enqueue ingest → (scan + extract) → <c>draft_ready</c> → review → approve → publish →
/// the foreground read path (<c>search_vendor_catalog</c>) returns ONLY the now-published items,
/// and never exposes anything before publish (Inv-16).
///
/// Ingestion runs through the real outbox publisher → transport → inbox → handler; the owner
/// lifecycle runs through the real Review/Approve/Publish handlers over a tenant-scoped context.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class MenuLifecycleEndToEndTests(PostgresFixture fixture)
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
        => new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<MessagingOptions>>(),
            NullLogger<OutboxPublisherHostedService>.Instance);

    private async Task NeutralizeExistingUnpublishedAsync()
    {
        await using var ctx = fixture.CreateContext("t11-e2e-cleanup");
        await ctx.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task FullLifecycle_IngestThroughPublish_ExposesOnlyPublishedItems()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t11-e2e-{Guid.NewGuid():N}";
        var catalogId = $"cat-{Guid.NewGuid():N}";

        // ── Seed an uploaded catalog + its ingest trigger. ──────────────────────────────────────
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            seedCtx.VendorCatalogs.Add(new VendorCatalog
            {
                TenantId = tenantId,
                CatalogId = catalogId,
                VersionLabel = "menu_e2e",
                Status = CatalogStatus.Uploaded,
                SourceHash = "sha256-test",
            });
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"msg-{Guid.NewGuid():N}",
                MessageType = "menu.commands.ingest.v1",
                CorrelationId = catalogId,
                Payload = $"{{\"catalog_id\":\"{catalogId}\",\"source_artifact_ref\":\"s3://tenant/menu.pdf\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        // ── Step 1: ingestion (scan + extract) drives uploaded → draft_ready. ───────────────────
        await using var sp = BuildContainer();
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var c = await ctx1.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
                .FirstAsync(x => x.TenantId == tenantId && x.CatalogId == catalogId);
            Assert.Equal(CatalogStatus.DraftReady, c.Status);

            // Pre-publish: the foreground read path exposes nothing (Inv-16).
            var hiddenWhileDraft = await new VendorCatalogRepository(ctx1)
                .SearchPublishedItemsAsync(null, 0, 50, CancellationToken.None);
            Assert.Empty(hiddenWhileDraft);
        }

        // ── Steps 2-4: review → approve → publish via the real handlers. ────────────────────────
        await RunReviewAsync(tenantId, catalogId);
        await RunApproveAsync(tenantId, catalogId);
        await RunPublishAsync(tenantId, catalogId);

        // ── Step 5: the published item is now (and only now) foreground-visible. ────────────────
        await using var assertCtx = fixture.CreateContext(tenantId);

        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(x => x.TenantId == tenantId && x.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.Published, catalog.Status);

        var visible = await new VendorCatalogRepository(assertCtx)
            .SearchPublishedItemsAsync(null, 0, 50, CancellationToken.None);
        Assert.Equal(["Sample Dish"], visible.Select(i => i.Name).ToList());
        Assert.All(visible, i => Assert.StartsWith($"{catalogId}:", i.ItemId));
    }

    private async Task RunReviewAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new ReviewCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditEventRepository(ctx), new UnitOfWork(ctx));
        await handler.Handle(
            new ReviewCatalogCommand(tenantId, catalogId, "owner", new List<CatalogItemEdit>()), CancellationToken.None);
    }

    private async Task RunApproveAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new ApproveCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditEventRepository(ctx), new UnitOfWork(ctx));
        await handler.Handle(new ApproveCatalogCommand(tenantId, catalogId, "owner"), CancellationToken.None);
    }

    private async Task RunPublishAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new PublishCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditEventRepository(ctx), new UnitOfWork(ctx));
        await handler.Handle(new PublishCatalogCommand(tenantId, catalogId, "owner"), CancellationToken.None);
    }
}
