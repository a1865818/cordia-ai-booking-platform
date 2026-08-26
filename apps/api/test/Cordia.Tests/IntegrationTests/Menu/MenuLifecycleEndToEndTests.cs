#pragma warning disable xUnit1051
using Cordia.Application.Common.Audit;
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
using Cordia.Tests.IntegrationTests.Messaging;
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
    private static ServiceProvider BuildContainer(string tenantId)
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
        services.ScopeOutboxClaimToTenant(tenantId);
        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
        => new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<MessagingOptions>>(),
            NullLogger<OutboxPublisherHostedService>.Instance);

    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    [Fact]
    public async Task FullLifecycle_IngestThroughPublish_ExposesOnlyPublishedItems()
    {
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
        await using var sp = BuildContainer(tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var c = await ctx1.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
                .FirstAsync(x => x.TenantId == tenantId && x.CatalogId == catalogId);
            Assert.Equal(CatalogStatus.DraftReady, c.Status);

            // Pre-publish: the foreground read path exposes nothing (Inv-16).
            var hiddenWhileDraft = await new VendorCatalogRepository(ctx1)
                .SearchPublishedItemsAsync(null, null, 0, 50, CancellationToken.None);
            Assert.Empty(hiddenWhileDraft);
        }

        // ── Steps 2-4: review (WITH a non-empty edit — the core §5.2 owner action; this 404'd
        //    before the review-then-edit ordering fix, review 2026-07-03 C6) → approve → publish. ──
        string draftItemId;
        await using (var draftCtx = fixture.CreateContext(tenantId))
        {
            draftItemId = await draftCtx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.TenantId == tenantId && i.CatalogId == catalogId)
                .Select(i => i.ItemId)
                .FirstAsync();
        }
        await RunReviewAsync(tenantId, catalogId,
            [new CatalogItemEdit(draftItemId, Name: null, Description: null, PriceMinor: 2500)]);
        await RunApproveAsync(tenantId, catalogId);
        await RunPublishAsync(tenantId, catalogId);

        // ── Step 5: the published item is now (and only now) foreground-visible, with the edit. ──
        await using var assertCtx = fixture.CreateContext(tenantId);

        var catalog = await assertCtx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(x => x.TenantId == tenantId && x.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.Published, catalog.Status);

        var visible = await new VendorCatalogRepository(assertCtx)
            .SearchPublishedItemsAsync(null, null, 0, 50, CancellationToken.None);
        Assert.Equal(["Sample Dish"], visible.Select(i => i.Name).ToList());
        Assert.All(visible, i => Assert.StartsWith($"{catalogId}:", i.ItemId));
        Assert.Equal(2500, visible.Single().PriceMinor); // the review edit survived to publish
    }

    private async Task RunReviewAsync(string tenantId, string catalogId, List<CatalogItemEdit>? edits = null)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new ReviewCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditWriter(new AuditEventRepository(ctx)), new UnitOfWork(ctx));
        await handler.Handle(
            new ReviewCatalogCommand(tenantId, catalogId, "owner", edits ?? [], "rc_test", "owner"), CancellationToken.None);
    }

    private async Task RunApproveAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new ApproveCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditWriter(new AuditEventRepository(ctx)), new UnitOfWork(ctx));
        await handler.Handle(new ApproveCatalogCommand(tenantId, catalogId, "owner", "rc_test", "owner"), CancellationToken.None);
    }

    private async Task RunPublishAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = new PublishCatalogCommandHandler(
            new VendorCatalogRepository(ctx), new AuditWriter(new AuditEventRepository(ctx)), new UnitOfWork(ctx));
        await handler.Handle(new PublishCatalogCommand(tenantId, catalogId, "owner", "rc_test", "owner"), CancellationToken.None);
    }
}
