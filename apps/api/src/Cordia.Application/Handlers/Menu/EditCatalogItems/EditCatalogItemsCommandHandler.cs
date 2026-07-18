using Cordia.Application.Common.Audit;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.EditCatalogItems;

/// <summary>
/// Applies owner item edits while the catalog is <c>in_review</c> (spec 11 §5.2), with no status
/// change — supports iterative correction before approval. Unknown catalog →
/// <see cref="CatalogNotFoundException"/> (404); a catalog not in <c>in_review</c> →
/// <see cref="CatalogNotEditableException"/> (409, enforcing draft immutability after approval).
/// </summary>
public sealed class EditCatalogItemsCommandHandler(
    IVendorCatalogRepository repo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<EditCatalogItemsCommand, Unit>
{
    public async Task<Unit> Handle(EditCatalogItemsCommand command, CancellationToken ct)
    {
        var catalog = await repo.GetAsync(command.CatalogId, ct)
            ?? throw new CatalogNotFoundException(command.CatalogId);

        if (catalog.Status != CatalogStatus.InReview)
            throw new CatalogNotEditableException(command.CatalogId, catalog.Status);

        var now = DateTimeOffset.UtcNow;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.ApplyItemEditsAsync(command.CatalogId, command.Edits, ct2);

            // Spec 14 §24.5 — owner action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: "owner",
                    ActorId: command.Actor,
                    Action: "catalog.items_edited",
                    TargetType: "vendor_catalog",
                    TargetId: command.CatalogId,
                    Decision: "allow",
                    DataClasses: ["catalog"],
                    ReasonCode: command.ReasonCode,
                    CorrelationId: command.CatalogId,
                    OccurredAt: now,
                    Extra: new { edit_count = command.Edits.Count }),
                ct2);
        }, ct);

        return Unit.Value;
    }
}
