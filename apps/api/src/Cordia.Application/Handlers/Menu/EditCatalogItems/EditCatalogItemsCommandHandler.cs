using System.Text.Json;
using Cordia.Domain.Entities;
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
    IAuditEventRepository auditRepo,
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

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "catalog.items_edited",
                Actor = command.Actor,
                SubjectRef = command.CatalogId,
                CorrelationId = command.CatalogId,
                OperationId = null,
                Metadata = JsonSerializer.Serialize(new { edit_count = command.Edits.Count }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }
}
