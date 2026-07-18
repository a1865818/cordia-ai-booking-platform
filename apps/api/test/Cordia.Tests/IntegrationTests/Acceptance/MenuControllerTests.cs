#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Alba HTTP-level tests for <c>MenuController</c> (spec 11 §5.2). Exercises the owner lifecycle
/// endpoints and asserts status codes / problem responses: upload → 202, review/approve/publish →
/// 200, unknown → 404, illegal transition → 409, missing tenant → 400.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class MenuControllerTests(AcceptanceApiFixture fixture)
{
    private async Task<string> SeedCatalogAsync(string tenantId, CatalogStatus status)
    {
        var catalogId = $"cat-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = catalogId,
            Status = status,
            SourceHash = "sha256-test",
        });
        await ctx.SaveChangesAsync();
        return catalogId;
    }

    [Fact]
    public async Task Upload_Returns202_CreatesUploadedCatalog_AndEnqueuesIngest()
    {
        var tenantId = $"acc-menu-up-{Guid.NewGuid():N}";

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post
                .RawJson("""{"source_artifact_ref":"s3://tenant/menu.pdf","version_label":"menu_v1"}""")
                .ToUrl("/api/menu/catalogs");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var catalogId = doc.RootElement.GetProperty("catalog_id").GetString()!;
        Assert.Equal("uploaded", doc.RootElement.GetProperty("status").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.NotNull(catalog);
        Assert.Equal(CatalogStatus.Uploaded, catalog!.Status);

        var outbox = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.MessageType == "menu.commands.ingest.v1" && m.CorrelationId == catalogId);
        Assert.NotNull(outbox);
    }

    [Fact]
    public async Task Review_DraftReady_Returns200()
    {
        var tenantId = $"acc-menu-rv-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.DraftReady);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/review");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.InReview, catalog.Status);
    }

    [Fact]
    public async Task EditItems_InReview_Returns200_AndAppliesEdit()
    {
        var tenantId = $"acc-menu-ed-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.InReview);

        // Seed a draft item (id namespaced as the ingestion handler does: {catalogId}:{itemId}).
        await using (var seed = fixture.CreateContext(tenantId))
        {
            seed.VendorCatalogItems.Add(new VendorCatalogItem
            {
                TenantId = tenantId,
                ItemId = $"{catalogId}:d1",
                CatalogId = catalogId,
                Name = "Old Name",
                Description = "Old desc",
                PriceMinor = 1000,
                Currency = "AUD",
            });
            await seed.SaveChangesAsync();
        }

        // Edits key on the full namespaced id the read path emits ({catalogId}:d1) — symmetric with
        // GetCatalogItem/Search (M5: no re-prefixing).
        await fixture.Host.Scenario(x =>
        {
            x.Post
                .RawJson($$"""{"edits":[{"item_id":"{{catalogId}}:d1","name":"New Name","price_minor":2500}]}""")
                .ToUrl($"/api/menu/catalogs/{catalogId}/edit");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var item = await ctx.VendorCatalogItems.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(i => i.TenantId == tenantId && i.ItemId == $"{catalogId}:d1");
        Assert.Equal("New Name", item.Name);
        Assert.Equal(2500, item.PriceMinor);
        Assert.Equal("Old desc", item.Description); // null field left unchanged

        // Catalog status is unchanged (edit does not transition).
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.InReview, catalog.Status);
    }

    [Fact]
    public async Task EditItems_UnknownItemId_Returns404_NotSilentNoOp()
    {
        // M5: an edit targeting an item id that matches no row in the editable catalog must SURFACE a
        // 404 (never silently succeed), so a stale/mistyped id fails loudly.
        var tenantId = $"acc-menu-ed404-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Post
                .RawJson($$"""{"edits":[{"item_id":"{{catalogId}}:missing","name":"New Name"}]}""")
                .ToUrl($"/api/menu/catalogs/{catalogId}/edit");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task EditItems_NotInReview_Returns409()
    {
        var tenantId = $"acc-menu-edc-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.DraftReady);

        await fixture.Host.Scenario(x =>
        {
            x.Post
                .RawJson("""{"edits":[{"item_id":"d1","name":"New Name"}]}""")
                .ToUrl($"/api/menu/catalogs/{catalogId}/edit");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Approve_InReview_Returns200()
    {
        var tenantId = $"acc-menu-ap-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/approve");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "alice");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.Approved, catalog.Status);
        Assert.Equal("alice", catalog.ApprovedBy);
    }

    [Fact]
    public async Task Publish_Approved_Returns200()
    {
        var tenantId = $"acc-menu-pb-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.Approved);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/publish");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.Published, catalog.Status);
    }

    [Fact]
    public async Task Review_UnknownCatalog_Returns404()
    {
        var tenantId = $"acc-menu-nf-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/does-not-exist/review");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task Approve_WrongStatus_Returns409()
    {
        var tenantId = $"acc-menu-cf-{Guid.NewGuid():N}";
        // Approve requires in_review; a draft_ready catalog is an illegal edge → 409.
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.DraftReady);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/approve");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner-1");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Approve_WithOperatorRole_Returns403_AndDoesNotAdvance()
    {
        // GD-1 role matrix: menu lifecycle requires OWNER; an operator assertion is denied
        // with 403 (AuthorizationFailedException) and a deny audit, and nothing advances.
        var tenantId = $"acc-menu-role-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/approve");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Reason-Code", "rc_test");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.InReview, catalog.Status);

        // The denial itself is on the audit trail (spec 14 §3.1 step 5 — deny audited too).
        var denyAudit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.EventType == "catalog.approved")
            .ToListAsync();
        Assert.Contains(denyAudit, e => e.Metadata.Contains("\"decision\":\"deny\"")
            || e.Metadata.Contains("role_not_satisfied"));
    }

    [Fact]
    public async Task Approve_MissingActorAndReasonHeaders_IsRejected()
    {
        // Spec 14 §24.5 fail-closed: owner actions without X-Actor / X-Reason-Code are rejected
        // (400 from the command validator) — never silently attributed to a default "owner".
        var tenantId = $"acc-menu-fc-{Guid.NewGuid():N}";
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.InReview);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/menu/catalogs/{catalogId}/approve");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        // And the catalog did NOT advance.
        await using var ctx = fixture.CreateContext(tenantId);
        var catalog = await ctx.VendorCatalogs.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.CatalogId == catalogId);
        Assert.Equal(CatalogStatus.InReview, catalog.Status);
    }

    [Fact]
    public async Task Review_MissingTenantHeader_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl("/api/menu/catalogs/anything/review");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }
}
