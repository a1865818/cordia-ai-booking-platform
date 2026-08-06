using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Services.Menu;

/// <summary>
/// Settles vendor catalogs stuck mid-ingestion into <see cref="CatalogStatus.ExtractFailed"/>
/// (docs §12 gap 8).
///
/// <para><b>It used to only report.</b> D4 froze <c>catalog_status</c> at nine values with no
/// <c>extract_failed</c>, so a stalled extraction could be counted and logged but never moved —
/// "genuinely failed" and "still working" stayed indistinguishable in the only field that records
/// catalog state. The D4 amendment (owner ruling 2026-08-06) added the tenth value, and this is
/// what drives rows into it.</para>
///
/// <para><b>Why a timeout is a legitimate verdict.</b> The threshold is deliberately generous and
/// matches the console's own staleness window, so operator and server never disagree about the
/// same catalog. A job that finishes after the threshold loses the version-guarded race and its
/// transition becomes a no-op — correct, because an extraction past the window is outside its SLA
/// whatever it eventually returns.</para>
///
/// <para>Terminal, not retryable: re-running extraction in place would mutate a catalog an
/// operator may already have judged. Recovery is a fresh upload, which supersedes.</para>
/// </summary>
public sealed class MenuIngestionMonitor(
    ApplicationDbContext db,
    IVendorCatalogRepository catalogs,
    IAuditEventRepository auditRepo,
    Metrics metrics,
    ILogger<MenuIngestionMonitor> logger) : IMenuIngestionMonitor
{
    /// <summary>The two statuses that are supposed to be transient. Anything else is a resting state.</summary>
    private static readonly CatalogStatus[] InProgress =
        [CatalogStatus.Scanning, CatalogStatus.Extracting];

    public async Task<int> SettleStalledAsync(
        DateTimeOffset stalledBefore,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        // IgnoreQueryFilters: a cross-tenant background entry point, like the retention sweeper.
        // It reports counts and identifiers to the operator's own logs and never returns catalog
        // CONTENT, so no tenant's data crosses into another's response (Inv-13).
        // Aged on StatusChangedAt, NOT CreatedAt. The threshold answers "how long has this been
        // stuck in scanning/extracting", and a catalog whose ingest message waited in a backlog
        // before starting has been mid-ingestion for none of that wait. Measured from creation it
        // would already be past due the moment extraction began, so the next tick would settle it
        // as terminally failed while the extractor was still working — and the extractor's own
        // version-guarded transition would then lose, discarding a completed extraction.
        var stalled = await db.VendorCatalogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => InProgress.Contains(c.Status) && c.StatusChangedAt < stalledBefore)
            .Where(c => tenantId == null || c.TenantId == tenantId)
            .Select(c => new { c.TenantId, c.CatalogId, c.Status, c.StatusChangedAt, c.Version })
            .ToListAsync(ct);

        if (stalled.Count == 0)
            return 0;

        var settled = 0;

        foreach (var c in stalled)
        {
            // Guarded by BOTH the expected status and the expected version: an extractor that
            // completed between the read above and this write has already moved the row, and must
            // win. Losing here is the intended outcome in that case, not an error.
            CatalogStateMachine.EnsureAllowed(c.Status, CatalogStatus.ExtractFailed);

            var rows = await catalogs.TryTransitionStatusAsync(
                c.TenantId, c.CatalogId, c.Version, c.Status, CatalogStatus.ExtractFailed, ct);

            if (rows == 0)
            {
                logger.LogDebug(
                    "Menu ingestion monitor: catalog={CatalogId} moved on before it could be failed; leaving it.",
                    c.CatalogId);
                continue;
            }

            settled++;

            // The SAME audit event IngestVendorMenuCommandHandler writes when it settles a
            // failure it caught itself. Both routes into `extract_failed` must leave the same
            // trail: without this, a catalog failed by the sweeper had a terminal status, a log
            // line and a metric, but nothing in `audit_events` — so the one route an operator
            // cannot have witnessed was also the one with no record to reconstruct it from.
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = c.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "catalog.extraction_failed",
                Actor = "system",
                SubjectRef = c.CatalogId,
                CorrelationId = c.CatalogId,
                Metadata = JsonSerializer.Serialize(new
                {
                    catalog_id = c.CatalogId,
                    reason = "stalled",
                    stuck_in = c.Status.ToString().ToLowerInvariant(),
                    stuck_since = c.StatusChangedAt,
                }),
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct);

            metrics.MenuIngestionStalledTotal.Add(
                1,
                new KeyValuePair<string, object?>(
                    Domain.Observability.MetricNames.Tags.State,
                    c.Status.ToString().ToLowerInvariant()));

            logger.LogWarning(
                "Menu ingestion failed: tenant={TenantId} catalog={CatalogId} was {Status} for "
                + "{AgeMinutes:F0} minute(s) and has been settled as extract_failed. Recovery is a "
                + "fresh upload, which supersedes it.",
                c.TenantId, c.CatalogId, c.Status,
                (DateTimeOffset.UtcNow - c.StatusChangedAt).TotalMinutes);
        }

        return settled;
    }
}
