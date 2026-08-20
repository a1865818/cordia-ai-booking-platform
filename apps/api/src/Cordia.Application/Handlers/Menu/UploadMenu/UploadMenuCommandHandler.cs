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
                // The ref is NOT opaque and this is NOT bytes-free (#95). For today's canonical
                // `inline:` form the ref *is* the uploaded document, and `source_artifact_ref` is on
                // SensitiveDataMask's control-plane EXEMPTION list (SensitiveDataMask.cs:94-99,
                // 173-174), so it is written to `outbox_messages.payload` — jsonb, no length cap —
                // verbatim and unscreened for PAN/CVV/SSN, as it is to any DLQ copy. The old comment
                // here asserted "no secrets/bytes — Inv-12", which is true of a storage key and
                // false of `inline:`.
                //
                // The exemption is DELIBERATE and pinned by cross-language vectors
                // (cordia_agent/security/redaction.py, tools/config/redaction-test-vectors.json with
                // "expect_exempt": true) — do not flip it here. The real fix is #84 putting artifacts
                // in object storage, after which the ref is genuinely an opaque key again and this
                // comment becomes true. Until then UploadMenuValidator bounds the damage
                // (MaxSourceArtifactRefChars) rather than removing it, and the mask still holds on
                // every LOG path: this is data at rest in two Postgres columns, not log leakage.
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
