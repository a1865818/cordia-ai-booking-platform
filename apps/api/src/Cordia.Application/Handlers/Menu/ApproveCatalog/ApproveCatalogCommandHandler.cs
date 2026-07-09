using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.ApproveCatalog;

/// <summary>
/// Drives <c>in_review → approved</c> (spec 11 §5.2), stamping <c>approved_by</c> / <c>approved_at</c>
/// in the same version-token-guarded update. Unknown catalog → <see cref="CatalogNotFoundException"/>
/// (404); not in <c>in_review</c> (or a lost race) → <see cref="InvalidCatalogStateTransitionException"/> (409).
/// </summary>
public sealed class ApproveCatalogCommandHandler(
    IVendorCatalogRepository repo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ApproveCatalogCommand, Unit>
{
    public async Task<Unit> Handle(ApproveCatalogCommand command, CancellationToken ct)
    {
        var catalog = await repo.GetAsync(command.CatalogId, ct)
            ?? throw new CatalogNotFoundException(command.CatalogId);

        CatalogStateMachine.EnsureAllowed(catalog.Status, CatalogStatus.Approved);

        var now = DateTimeOffset.UtcNow;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var rows = await repo.TryApproveAsync(
                command.TenantId, command.CatalogId, catalog.Version, command.Actor, now, ct2);
            if (rows == 0)
                throw new InvalidCatalogStateTransitionException(catalog.Status, CatalogStatus.Approved);

            // Owner-confirmed → strip the draft-only extraction-agent metadata so the approved (and
            // later published) rows never retain it (§3, Inv-16).
            await repo.ClearDraftExtractionMetadataAsync(command.CatalogId, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "catalog.approved",
                Actor = command.Actor,
                SubjectRef = command.CatalogId,
                CorrelationId = command.CatalogId,
                OperationId = null,
                Metadata = JsonSerializer.Serialize(new { approved_by = command.Actor, approved_at = now }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return Unit.Value;
    }
}
