#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services.Menu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Server-side detection of stalled menu ingestion (docs §12 gap 8).
///
/// <para>The D4 amendment (owner ruling 2026-08-06) added <c>extract_failed</c>, so the monitor
/// SETTLES a stalled catalog into it rather than merely counting one. That makes the staleness
/// threshold a destructive verdict — recovery is a fresh upload — so these tests pin both
/// directions: a genuinely stalled row is failed, and a row that is merely OLD but has not been
/// mid-ingestion for long is left strictly alone.</para>
///
/// <para>Before it existed, staleness was computed in the console only, so a stuck extraction was
/// invisible unless an operator opened the menu screen.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class MenuIngestionMonitorTests(AcceptanceApiFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seeds a catalog that ENTERED <paramref name="status"/> at <paramref name="statusChangedAt"/>.
    /// <paramref name="createdAt"/> defaults to the same instant — the common case — but the two
    /// are separable, because the difference between them is exactly the bug the monitor's clock
    /// choice creates or avoids.
    /// </summary>
    private async Task<string> SeedCatalogAsync(
        string tenantId,
        CatalogStatus status,
        DateTimeOffset statusChangedAt,
        DateTimeOffset? createdAt = null)
    {
        var catalogId = $"cat-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = "v1",
            Status = status,
            SourceHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = createdAt ?? statusChangedAt,
            StatusChangedAt = statusChangedAt,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return catalogId;
    }

    private async Task<int> RunAsync(string tenantId, DateTimeOffset stalledBefore)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var monitor = new MenuIngestionMonitor(
            ctx,
            new Cordia.Infrastructure.Repositories.VendorCatalogRepository(ctx),
            new Cordia.Infrastructure.Repositories.AuditEventRepository(ctx),
            new Metrics(),
            NullLogger<MenuIngestionMonitor>.Instance);
        return await monitor.SettleStalledAsync(stalledBefore, tenantId);
    }

    private async Task<CatalogStatus> ReadStatusAsync(string tenantId, string catalogId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var row = ctx.VendorCatalogs.Single(c => c.CatalogId == catalogId);
        await Task.CompletedTask;
        return row.Status;
    }

    [Theory]
    [InlineData(CatalogStatus.Scanning)]
    [InlineData(CatalogStatus.Extracting)]
    public async Task ReportsACatalogStuckInATransientState(CatalogStatus status)
    {
        var tenantId = TestIds.New("menu-stall");
        await SeedCatalogAsync(tenantId, status, Now.AddHours(-2));

        Assert.Equal(1, await RunAsync(tenantId, Now.AddMinutes(-15)));
    }

    /// <summary>
    /// The monitor now SETTLES the row, where before the D4 amendment it could only count: with
    /// no <c>extract_failed</c> value to move to, "genuinely failed" and "still working" were the
    /// same state. Owner ruling 2026-08-06 added the value; this is what drives rows into it.
    /// </summary>
    [Fact]
    public async Task SettlesAStalledCatalogAsExtractFailed()
    {
        var tenantId = TestIds.New("menu-stall-settle");
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.Extracting, Now.AddHours(-2));

        await RunAsync(tenantId, Now.AddMinutes(-15));

        Assert.Equal(CatalogStatus.ExtractFailed, await ReadStatusAsync(tenantId, catalogId));
    }

    /// <summary>
    /// A catalog inside the window must be left strictly alone — not merely unreported. Failing a
    /// job that is still running is worse than the invisibility this replaced.
    /// </summary>
    [Fact]
    public async Task LeavesAYoungCatalogsStatusAlone()
    {
        var tenantId = TestIds.New("menu-stall-young-state");
        var catalogId = await SeedCatalogAsync(tenantId, CatalogStatus.Extracting, Now.AddMinutes(-1));

        await RunAsync(tenantId, Now.AddMinutes(-15));

        Assert.Equal(CatalogStatus.Extracting, await ReadStatusAsync(tenantId, catalogId));
    }

    /// <summary>
    /// The clock is time-at-STATUS, not age-since-upload — and getting this wrong destroys work.
    ///
    /// <para>A catalog uploaded two hours ago whose ingest message sat in a queue backlog, and
    /// which started extracting one minute ago, is perfectly healthy. Measured from
    /// <c>CreatedAt</c> it is already an hour and three quarters past due the instant extraction
    /// begins, so the very next tick settles it as terminally failed — and the extractor still
    /// running then loses its own version-guarded transition and throws a completed extraction
    /// away. Nothing about that is visible afterwards: the row just reads <c>extract_failed</c>.</para>
    /// </summary>
    [Fact]
    public async Task LeavesAnOldCatalogThatJustEnteredExtractingAlone()
    {
        var tenantId = TestIds.New("menu-stall-late-start");
        var catalogId = await SeedCatalogAsync(
            tenantId,
            CatalogStatus.Extracting,
            statusChangedAt: Now.AddMinutes(-1),
            createdAt: Now.AddHours(-2));

        Assert.Equal(0, await RunAsync(tenantId, Now.AddMinutes(-15)));
        Assert.Equal(CatalogStatus.Extracting, await ReadStatusAsync(tenantId, catalogId));
    }

    /// <summary>
    /// ExtractFailed is terminal: a settled catalog is not re-settled on the next tick, so the
    /// counter measures failures rather than sweeps.
    /// </summary>
    [Fact]
    public async Task DoesNotReSettleAnAlreadyFailedCatalog()
    {
        var tenantId = TestIds.New("menu-stall-terminal");
        await SeedCatalogAsync(tenantId, CatalogStatus.Extracting, Now.AddHours(-2));

        Assert.Equal(1, await RunAsync(tenantId, Now.AddMinutes(-15)));
        Assert.Equal(0, await RunAsync(tenantId, Now.AddMinutes(-15)));
    }

    /// <summary>A young extraction is slow, not stuck — reporting it would train operators to ignore this.</summary>
    [Fact]
    public async Task DoesNotReportACatalogInsideTheWindow()
    {
        var tenantId = TestIds.New("menu-stall-young");
        await SeedCatalogAsync(tenantId, CatalogStatus.Extracting, Now.AddMinutes(-1));

        Assert.Equal(0, await RunAsync(tenantId, Now.AddMinutes(-15)));
    }

    /// <summary>
    /// Only the two states that are SUPPOSED to be transient count. `draft_ready` waiting on a
    /// human reviewer is a resting state — it can sit for days and nothing is wrong.
    /// </summary>
    [Theory]
    [InlineData(CatalogStatus.DraftReady)]
    [InlineData(CatalogStatus.Published)]
    [InlineData(CatalogStatus.Approved)]
    public async Task DoesNotReportARestingState(CatalogStatus status)
    {
        var tenantId = TestIds.New("menu-stall-resting");
        await SeedCatalogAsync(tenantId, status, Now.AddDays(-30));

        Assert.Equal(0, await RunAsync(tenantId, Now.AddMinutes(-15)));
    }
}
