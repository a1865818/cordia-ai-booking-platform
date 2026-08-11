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
    IOutboxRepository outboxRepo,
    IUnitOfWork uow,
    Metrics metrics,
    ILogger<MenuIngestionMonitor> logger) : IMenuIngestionMonitor
{
    /// <summary>
    /// The owner-facing alert, bound to the SAME Domain constant
    /// <c>IngestVendorMenuCommandHandler</c>'s synchronous failure path uses — one message type
    /// for one meaning, whichever route settled it. It was a duplicated literal held together only
    /// by a comment, which the compiler could not check; an unregistered type dead-letters, so a
    /// version bump on one side would have silently stopped these alerts reaching anyone.
    /// </summary>
    private const string ExtractionFailedEventType = Cordia.Domain.Menu.MenuMessageTypes.ExtractionFailed;

    /// <summary>
    /// The statuses that are supposed to be transient. Anything else is a resting state.
    ///
    /// <para>Materialised from <see cref="CatalogStateMachine.InProgressStates"/> — an array because
    /// EF translates <c>array.Contains(column)</c> into SQL <c>= ANY</c> — so the sweep and the
    /// state machine cannot disagree about which statuses represent a job that may never report
    /// back.</para>
    /// </summary>
    private static readonly CatalogStatus[] InProgress =
        [.. CatalogStateMachine.InProgressStates];

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

            // The status write, the owner alert and the audit row commit TOGETHER.
            //
            // They were three separate commits: the version-guarded status update goes straight to
            // the database, and the outbox/audit inserts came after. A failure between them left a
            // catalog terminally `extract_failed` with nobody told and nothing recorded — and the
            // row is terminal, so no later sweep selects it again to finish the job. One
            // transaction makes the settlement all-or-nothing; a failure simply leaves the catalog
            // where it was, for the next tick to settle properly.
            var rows = 0;

            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                rows = await catalogs.TryTransitionStatusAsync(
                    c.TenantId, c.CatalogId, c.Version, c.Status, CatalogStatus.ExtractFailed, ct2);

                if (rows == 0)
                    return;

                await WriteSettlementRecordsAsync(c.TenantId, c.CatalogId, c.Status, c.StatusChangedAt, ct2);
            }, ct);

            if (rows == 0)
            {
                logger.LogDebug(
                    "Menu ingestion monitor: catalog={CatalogId} moved on before it could be failed; leaving it.",
                    c.CatalogId);
                continue;
            }

            settled++;

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

    /// <summary>
    /// The owner alert and the audit row that accompany a settled catalog — the SAME pair
    /// <c>IngestVendorMenuCommandHandler</c> writes when it settles a failure it caught itself.
    /// Both routes into <c>extract_failed</c> must leave the same trail and make the same noise;
    /// this route is the one an operator has no other way of learning about, because it exists for
    /// the worker that died before it could report anything (Inv-14).
    /// </summary>
    private async Task WriteSettlementRecordsAsync(
        string tenantId,
        string catalogId,
        CatalogStatus stuckIn,
        DateTimeOffset stuckSince,
        CancellationToken ct)
    {
        await outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = ExtractionFailedEventType,
            CorrelationId = catalogId,
            Payload = JsonSerializer.Serialize(new { catalog_id = catalogId, reason = "stalled" }),
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        await auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = "catalog.extraction_failed",
            Actor = "system",
            SubjectRef = catalogId,
            CorrelationId = catalogId,
            Metadata = JsonSerializer.Serialize(new
            {
                catalog_id = catalogId,
                reason = "stalled",
                stuck_in = stuckIn.ToString().ToLowerInvariant(),
                stuck_since = stuckSince,
            }),
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}
