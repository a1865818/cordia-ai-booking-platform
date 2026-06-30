using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.ReviewCatalog;

/// <summary>
/// Drives <c>draft_ready → in_review</c> (spec 11 §5.2), applying any owner edits to draft items
/// first, in one transaction. Unknown catalog → <see cref="CatalogNotFoundException"/> (404); a
/// catalog not in <c>draft_ready</c> (or a lost version-token race) →
/// <see cref="InvalidCatalogStateTransitionException"/> (409).
/// </summary>
public sealed class ReviewCatalogCommandHandler(
    IVendorCatalogRepository repo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ReviewCatalogCommand, Unit>
{
    public async Task<Unit> Handle(ReviewCatalogCommand command, CancellationToken ct)
    {
        var catalog = await repo.GetAsync(command.CatalogId, ct)
            ?? throw new CatalogNotFoundException(command.CatalogId);

        // 409 immediately when the catalog is not in a reviewable state.
        CatalogStateMachine.EnsureAllowed(catalog.Status, CatalogStatus.InReview);

        var now = DateTimeOffset.UtcNow;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            if (command.Edits.Count > 0)
                await repo.ApplyItemEditsAsync(command.CatalogId, command.Edits, ct2);

            var rows = await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, catalog.Version,
                CatalogStatus.DraftReady, CatalogStatus.InReview, ct2);
            if (rows == 0)
                throw new InvalidCatalogStateTransitionException(catalog.Status, CatalogStatus.InReview);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "catalog.in_review",
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
