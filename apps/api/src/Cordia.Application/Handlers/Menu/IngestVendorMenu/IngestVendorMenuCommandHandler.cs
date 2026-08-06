using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Menu.IngestVendorMenu;

/// <summary>
/// Drives the menu-ingestion pipeline (spec 11 §5.2) inside the <c>InboxDeduplicationBehavior</c>
/// transaction: <c>Uploaded → Scanning → {Quarantined | Extracting → DraftReady}</c>.
///
/// <para><b>Runs inside the inbox transaction.</b> Does NOT open a new <c>IUnitOfWork</c>
/// transaction — repository writes share the ambient scoped DbContext and commit together.</para>
///
/// <para><b>Fails securely.</b> A malware hit settles to <c>Quarantined</c> (no extraction ever
/// runs — Inv prompt-injection/§27.6 + FM#4); an extraction failure is caught and the catalog is
/// settled to <c>ExtractFailed</c> (the tenth D4 value, owner ruling 2026-08-06) — version-guarded,
/// so a concurrent completion wins. The handler never throws for an expected failure, so the inbox
/// transaction commits and the message is not redelivered into a poison loop.</para>
/// </summary>
public sealed class IngestVendorMenuCommandHandler(
    IVendorCatalogRepository repo,
    IArtifactScanner scanner,
    IIsolatedMenuExtractor extractor,
    IAuditEventRepository auditRepo,
    IOutboxRepository outboxRepo,
    ILogger<IngestVendorMenuCommandHandler> logger)
    : IRequestHandler<IngestVendorMenuCommand, Unit>
{
    /// <summary>Owner-facing alert emitted when an artifact is quarantined by the malware scan (Inv-14, §8).</summary>
    public const string QuarantinedEventType = "menu.catalog.quarantined.v1";

    /// <summary>
    /// Owner-facing alert emitted when extraction fails and the catalog is settled to
    /// <c>ExtractFailed</c> (Inv-14, §8). A terminal status the owner never looks at is not a
    /// notification.
    ///
    /// <para>Bound to the Domain constant rather than repeating the literal:
    /// <c>MenuIngestionMonitor</c> emits the same alert from Infrastructure, which cannot
    /// reference Application, so the two had separate copies that only a comment held together.</para>
    /// </summary>
    public const string ExtractionFailedEventType = Cordia.Domain.Menu.MenuMessageTypes.ExtractionFailed;


    public async Task<Unit> Handle(IngestVendorMenuCommand command, CancellationToken ct)
    {
        var catalog = await repo.GetAsync(command.CatalogId, ct);
        if (catalog is null || catalog.TenantId != command.TenantId)
        {
            logger.LogWarning(
                "IngestVendorMenu: catalog {CatalogId} not found or wrong tenant {TenantId}; ack-and-drop.",
                command.CatalogId, command.TenantId);
            return Unit.Value;
        }

        if (catalog.Status != CatalogStatus.Uploaded)
        {
            logger.LogWarning(
                "IngestVendorMenu: catalog {CatalogId} in unexpected status {Status}; ack-and-drop.",
                command.CatalogId, catalog.Status);
            return Unit.Value;
        }

        var now = DateTimeOffset.UtcNow;
        var version = catalog.Version;

        // Uploaded → Scanning
        CatalogStateMachine.EnsureAllowed(CatalogStatus.Uploaded, CatalogStatus.Scanning);
        if (await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, version,
                CatalogStatus.Uploaded, CatalogStatus.Scanning, ct) == 0)
        {
            logger.LogWarning("IngestVendorMenu: lost race Uploaded→Scanning for {CatalogId}.", command.CatalogId);
            return Unit.Value;
        }
        version++;

        // Malware scan before any extraction (FM#4).
        var scan = await scanner.ScanAsync(command.SourceArtifactRef, ct);
        if (scan == ArtifactScanResult.Detected)
        {
            CatalogStateMachine.EnsureAllowed(CatalogStatus.Scanning, CatalogStatus.Quarantined);
            var quarantined = await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, version,
                CatalogStatus.Scanning, CatalogStatus.Quarantined, ct);

            if (quarantined > 0)
            {
                await WriteAuditAsync(command, "catalog.quarantined", new { catalog_id = command.CatalogId }, now, ct);
                // Surface the quarantine to the owner (Inv-14): the artifact is never extracted/published.
                await EnqueueOwnerAlertAsync(command, QuarantinedEventType, "malware_detected", now, ct);
            }
            else
            {
                logger.LogWarning("IngestVendorMenu: lost race Scanning→Quarantined for {CatalogId}.", command.CatalogId);
            }

            return Unit.Value; // malware is NEVER extracted
        }

        // Clean → Extracting
        CatalogStateMachine.EnsureAllowed(CatalogStatus.Scanning, CatalogStatus.Extracting);
        if (await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, version,
                CatalogStatus.Scanning, CatalogStatus.Extracting, ct) == 0)
        {
            logger.LogWarning("IngestVendorMenu: lost race Scanning→Extracting for {CatalogId}.", command.CatalogId);
            return Unit.Value;
        }
        version++;

        // Isolated extraction (fail-secure: never throw inside the inbox txn → no poison redelivery).
        IReadOnlyList<DraftCatalogItem> drafts;
        try
        {
            drafts = await extractor.ExtractAsync(command.SourceArtifactRef, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Settled HERE, not left for the sweeper.
            //
            // The D4 amendment (owner ruling 2026-08-06) added `extract_failed`, and this is the
            // one place that already KNOWS the extraction failed. Leaving the row in Extracting
            // for MenuIngestionMonitor to infer from a staleness threshold meant the console told
            // the operator "being read" for up to StalenessSeconds after the platform had a
            // definite answer — a confident falsehood on a fact already in hand (Inv-16). The
            // monitor stays as the backstop for the case it is actually for: a worker that died
            // mid-extraction and will never reach this catch at all.
            //
            // Fail-secure: never throw inside the inbox txn, or the rollback un-admits the message
            // and it comes back as poison (Inv-5). A lost race is not an error — the monitor or a
            // concurrent completion already moved the row, and that writer wins.
            CatalogStateMachine.EnsureAllowed(CatalogStatus.Extracting, CatalogStatus.ExtractFailed);
            var failed = await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, version,
                CatalogStatus.Extracting, CatalogStatus.ExtractFailed, ct);

            if (failed > 0)
            {
                logger.LogWarning(
                    ex,
                    "IngestVendorMenu: extraction failed for {CatalogId}; settled as extract_failed. "
                    + "Recovery is a fresh upload, which supersedes it.",
                    command.CatalogId);

                // Gated on winning the CAS, exactly like the quarantine and draft-ready paths.
                //
                // Emitted unconditionally, these two spoke for a transition this call did not make.
                // `MenuIngestionMonitor` writes the same `catalog.extraction_failed` audit when it
                // settles a row stalled in Extracting, so a slow-then-failed extraction — the
                // monitor settles it, then this catch finally runs — wrote the audit twice and sent
                // the owner a second alert for one failure. Losing the race means another writer
                // owns the outcome and has already said so.
                await WriteAuditAsync(command, "catalog.extraction_failed", new { catalog_id = command.CatalogId }, now, ct);
                // Still alerted (Inv-14): a terminal status the owner never looks at is not a notification.
                await EnqueueOwnerAlertAsync(command, ExtractionFailedEventType, "extraction_failed", now, ct);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "IngestVendorMenu: extraction failed for {CatalogId} but the row had already moved; "
                    + "leaving it where the winner put it.",
                    command.CatalogId);
            }

            return Unit.Value;
        }

        // Persist draft items (item ids namespaced by catalog to stay unique per tenant).
        var items = drafts
            .Select(d => new VendorCatalogItem
            {
                TenantId = command.TenantId,
                ItemId = $"{command.CatalogId}:{d.ItemId}",
                CatalogId = command.CatalogId,
                Name = d.Name,
                Description = d.Description,
                PriceMinor = d.PriceMinor,
                Currency = d.Currency,
                Category = d.Category,
                Modifiers = d.Modifiers,
                AvailabilityWindow = d.AvailabilityWindow,
                ImageRef = d.ImageRef,
                ExtractionConfidence = d.ExtractionConfidence,
                SourceLocation = d.SourceLocation,
            })
            .ToList();
        await repo.AddDraftItemsAsync(items, ct);

        // Extracting → DraftReady
        CatalogStateMachine.EnsureAllowed(CatalogStatus.Extracting, CatalogStatus.DraftReady);
        var draftReady = await repo.TryTransitionStatusAsync(
            command.TenantId, command.CatalogId, version,
            CatalogStatus.Extracting, CatalogStatus.DraftReady, ct);

        if (draftReady > 0)
            await WriteAuditAsync(command, "catalog.draft_ready", new { catalog_id = command.CatalogId, item_count = items.Count }, now, ct);
        else
            logger.LogWarning("IngestVendorMenu: lost race Extracting→DraftReady for {CatalogId}.", command.CatalogId);

        return Unit.Value;
    }

    /// <summary>
    /// Enqueues an owner-facing alert on the transactional outbox (inside the inbox transaction, Inv-4)
    /// so a failed/quarantined ingestion is never silently dropped (Inv-14, §8). The payload carries only
    /// the catalog id + a coarse reason — no artifact bytes, URL, or secret (Inv-12). The message routes to
    /// the <c>menu.commands</c> lane; until an owner-notification consumer is wired it is safely ack-delivered.
    /// </summary>
    private Task EnqueueOwnerAlertAsync(
        IngestVendorMenuCommand command, string messageType, string reason, DateTimeOffset now, CancellationToken ct)
        => outboxRepo.AddAsync(new OutboxMessage
        {
            TenantId = command.TenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = messageType,
            CorrelationId = command.CorrelationId ?? command.CatalogId,
            CausationId = command.MessageId,
            Payload = JsonSerializer.Serialize(new { catalog_id = command.CatalogId, reason }),
            OccurredAt = now,
        }, ct);

    private Task WriteAuditAsync(
        IngestVendorMenuCommand command, string eventType, object metadata, DateTimeOffset now, CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = command.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = command.CatalogId,
            CorrelationId = command.CorrelationId,
            OperationId = null,
            Metadata = JsonSerializer.Serialize(metadata),
            OccurredAt = now,
        }, ct);
}
