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
/// left in <c>Extracting</c> (there is no <c>extract_failed</c> value in the frozen D4
/// <c>catalog_status</c> enum) and surfaced via an audit event (Inv-14). The handler never throws
/// for an expected failure, so the inbox transaction commits and the message is not redelivered
/// into a poison loop.</para>
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

    /// <summary>Owner-facing alert emitted when extraction fails (catalog stuck in Extracting; Inv-14, §8).</summary>
    public const string ExtractionFailedEventType = "menu.catalog.extraction_failed.v1";


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
            // No 'extract_failed' state (frozen D4): leave the catalog in Extracting (never Published)
            // and surface the failure via audit + the stuck state (Inv-14).
            logger.LogWarning(ex, "IngestVendorMenu: extraction failed for {CatalogId}; left in Extracting.", command.CatalogId);
            await WriteAuditAsync(command, "catalog.extraction_failed", new { catalog_id = command.CatalogId }, now, ct);
            // Surface the failure to the owner (Inv-14): there is no extract_failed state (frozen D4), so the
            // owner alert + the stuck Extracting status + the audit row are how a failed extraction is made visible.
            await EnqueueOwnerAlertAsync(command, ExtractionFailedEventType, "extraction_failed", now, ct);
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
