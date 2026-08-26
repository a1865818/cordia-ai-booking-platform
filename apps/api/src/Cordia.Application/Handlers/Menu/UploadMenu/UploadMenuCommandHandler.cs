using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.UploadMenu;

/// <summary>
/// Creates the <c>uploaded</c> catalog row and enqueues the ingestion trigger in one transaction
/// (spec 11 §5.2). The artifact is NOT read here — extraction happens later in the isolated
/// consumer; <c>SourceHash</c> is populated by that pipeline, so it is left empty at upload.
/// </summary>
public sealed class UploadMenuCommandHandler(
    IVendorCatalogRepository repo,
    IOutboxRepository outboxRepo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<UploadMenuCommand, string>
{
    public async Task<string> Handle(UploadMenuCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var catalogId = $"cat_{Guid.NewGuid():N}";
        var messageId = $"obx_{Guid.NewGuid():N}";

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.AddCatalogAsync(new VendorCatalog
            {
                TenantId = command.TenantId,
                CatalogId = catalogId,
                VersionLabel = command.VersionLabel,
                Status = CatalogStatus.Uploaded,
                // Computed by the scan/extraction pipeline; unknown at upload time.
                SourceHash = string.Empty,
            }, ct2);

            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = messageId,
                MessageType = "menu.commands.ingest.v1",
                CorrelationId = catalogId,
                // Payload carries the catalog id + the artifact ref.
                //
                // The ref is NOT opaque. For today's canonical `inline:` form the ref *is* the
                // uploaded document — InlineMenuArtifactTextResolver resolves only `inline:` and
                // `data:text/plain`, and object-store schemes arrive with #84.
                //
                // #95, FIXED: `source_artifact_ref` used to sit on SensitiveDataMask's control-plane
                // exemption list, so this payload reached `outbox_messages.payload` (jsonb, no
                // length cap) verbatim and unscreened for PAN/CVV/SSN — the Inv-12 choke point in
                // OutboxRepository.AddAsync ran and was a deliberate no-op for this one key. The key
                // is off the list now, in BOTH lanes (SensitiveDataMask.cs and
                // cordia_agent/security/redaction.py), with the shared vector flipped to
                // "expect_exempt": false. Masking replaces only detected spans, so ordinary menu
                // content still round-trips for the consumer that reads this ref back out —
                // MenuArtifactRedactionTests pins both halves against the persisted row.
                //
                // provider_ref and the streaming refs are still exempt, on purpose: they are
                // genuinely system-minted, and masking a Luhn-coincidental one would break the
                // §16.2 reconciliation joins. Exactly one key left the list.
                //
                // #84 remains the structural fix — once artifacts live in object storage the ref is
                // an opaque key again and there is no document here to mask. UploadMenuValidator's
                // MaxSourceArtifactRefChars still bounds payload size.
                Payload = JsonSerializer.Serialize(new
                {
                    catalog_id = catalogId,
                    source_artifact_ref = command.SourceArtifactRef,
                }),
                OccurredAt = now,
            }, ct2);

            // Spec 14 §24.5 — owner action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: "owner",
                    ActorId: command.Actor,
                    Action: "catalog.uploaded",
                    TargetType: "vendor_catalog",
                    TargetId: catalogId,
                    Decision: "allow",
                    DataClasses: ["catalog"],
                    ReasonCode: command.ReasonCode,
                    CorrelationId: catalogId,
                    OccurredAt: now,
                    Extra: new { version_label = command.VersionLabel }),
                ct2);
        }, ct);

        return catalogId;
    }
}
