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

    /// <summary>
    /// Most flags serialized into the <c>catalog.extraction_flagged</c> audit row. The flag list
    /// comes back from the extraction agent, whose input is an untrusted document, so its length is
    /// attacker-influenced: a response carrying one flag per line of a large menu would otherwise
    /// write an unbounded audit row. The true total is always recorded as <c>flag_count</c>, so the
    /// cap truncates the sample, never the signal.
    /// </summary>
    private const int MaxAuditedFlags = 50;

    /// <summary>
    /// Character bound on each agent-minted string (item id, reason) copied into that audit row —
    /// the count cap alone does not bound the row if one reason can be a megabyte.
    /// </summary>
    private const int MaxAuditedFlagFieldChars = 200;


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
        MenuExtractionResult extraction;
        try
        {
            extraction = await extractor.ExtractAsync(command.SourceArtifactRef, ct);
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
        var items = extraction.Items
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
        {
            // Gated on the SAME CAS as draft_ready, and for the same reason the extract_failed catch
            // above is gated: an audit event speaks for a transition, and this call only made the
            // Extracting→DraftReady one if it won. Losing the race means another writer (typically
            // MenuIngestionMonitor settling a stalled row to ExtractFailed) owns the outcome, and a
            // "these items need your review" record against a catalog that will never be reviewed is
            // a confident falsehood (Inv-16), not a warning.
            //
            // Advisory only ("flag, never act"): the flags change nothing about the transition, the
            // items still land as drafts, and DraftReady is the only success edge out of Extracting —
            // nothing here can approve or publish.
            if (extraction.Flags.Count > 0)
            {
                logger.LogWarning(
                    "IngestVendorMenu: extraction raised {FlagCount} review flag(s) for {CatalogId}; "
                    + "items are drafts awaiting owner review. Reasons are in the catalog.extraction_flagged audit.",
                    extraction.Flags.Count, command.CatalogId);

                // Metadata carries ONLY the catalog id, the true flag count, and a bounded sample of
                // {item_id, reason} — the two agent-minted labels. Never the menu text, never the
                // artifact ref (SensitiveDataMask treats source_artifact_ref as a control-plane key and
                // copies it verbatim, and its inline form IS the document), never anything else derived
                // from the untrusted upload.
                //
                // UNTRUSTED VALUES. The keys are fixed, but `reason` and `item_id` are free text the
                // model produced while reading an attacker-supplied document — a document can steer it
                // into echoing itself back through them. They are bounded and stripped here
                // (SanitizeAuditLabel) and PAN-masked at the AuditEventSanitizationInterceptor choke
                // point, but a console rendering this row must still ESCAPE them, not trust them.
                await WriteAuditAsync(command, "catalog.extraction_flagged", new
                {
                    catalog_id = command.CatalogId,
                    flag_count = extraction.Flags.Count,
                    flags = extraction.Flags
                        .Take(MaxAuditedFlags)
                        .Select(f => new { item_id = SanitizeAuditLabel(f.ItemId), reason = SanitizeAuditLabel(f.Reason) })
                        .ToArray(),
                }, now, ct);
            }

            await WriteAuditAsync(command, "catalog.draft_ready", new { catalog_id = command.CatalogId, item_count = items.Count }, now, ct);
        }
        else
        {
            // The audit row is gated on the CAS (above), but the SIGNAL is not lost with the race.
            // `suspicious_content` — "this upload carried prompt-injection markers" — is worth knowing
            // whether or not the catalog is still reviewable, and the incentive gradient runs the wrong
            // way: extraction is slowest on large hostile documents, which are exactly the ones the
            // monitor is most likely to settle first. So the reasons are logged here even though no
            // audit row is written.
            logger.LogWarning(
                "IngestVendorMenu: lost race Extracting→DraftReady for {CatalogId}; "
                + "{FlagCount} extraction flag(s) go unaudited: {FlagReasons}.",
                command.CatalogId,
                extraction.Flags.Count,
                extraction.Flags.Take(MaxAuditedFlags).Select(f => SanitizeAuditLabel(f.Reason)).ToArray());
        }

        return Unit.Value;
    }

    /// <summary>
    /// Bounds one agent-minted label before it is copied into an audit row: control characters
    /// dropped, then truncated to <see cref="MaxAuditedFlagFieldChars"/>.
    ///
    /// <para><b>The control-character strip is a poison-loop guard, not cosmetics.</b> This is the
    /// first site in the codebase that copies model-generated free text into <c>AuditEvent.Metadata</c>
    /// (every other write site serializes fixed literals). <c>JsonSerializer</c> happily encodes an
    /// embedded NUL (<c>U+0000</c>) as a six-character \u0000 escape, which PostgreSQL
    /// rejects for a <c>jsonb</c> column (<c>22P05 unsupported Unicode escape sequence</c>).
    /// That throw would happen at
    /// <c>SaveChanges</c> — OUTSIDE this handler and outside its fail-secure catch — so the inbox
    /// transaction would roll back, the admission would be undone, and the message would redeliver
    /// forever, re-extracting the same hostile document each time (Inv-5). Wrapping the audit write in
    /// a try/catch would not help; <c>AddAsync</c> only stages the entity. A control character in an
    /// operator-facing label has no legitimate meaning, so all of them are dropped, not just NUL.</para>
    /// </summary>
    private static string? SanitizeAuditLabel(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var cleaned = value.Any(char.IsControl)
            ? new string(value.Where(c => !char.IsControl(c)).ToArray())
            : value;

        return cleaned.Length > MaxAuditedFlagFieldChars ? cleaned[..MaxAuditedFlagFieldChars] : cleaned;
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
