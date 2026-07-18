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
                // Payload carries only the catalog id + opaque artifact ref (no secrets/bytes — Inv-12).
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
