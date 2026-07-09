using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.PublishCatalog;

/// <summary>
/// Drives <c>approved → published</c> (spec 11 §5.2). Within one transaction it first supersedes the
/// tenant's prior live published version (if any) — <c>published → superseded</c> — then publishes
/// this catalog, so exactly one version is ever live. Unknown catalog →
/// <see cref="CatalogNotFoundException"/> (404); not in <c>approved</c> (or a lost race) →
/// <see cref="InvalidCatalogStateTransitionException"/> (409).
/// </summary>
public sealed class PublishCatalogCommandHandler(
    IVendorCatalogRepository repo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<PublishCatalogCommand, Unit>
{
    public async Task<Unit> Handle(PublishCatalogCommand command, CancellationToken ct)
    {
        var catalog = await repo.GetAsync(command.CatalogId, ct)
            ?? throw new CatalogNotFoundException(command.CatalogId);

        CatalogStateMachine.EnsureAllowed(catalog.Status, CatalogStatus.Published);

        var now = DateTimeOffset.UtcNow;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Supersede the prior live published version first (only one is live per tenant).
            var prior = await repo.GetCurrentPublishedAsync(command.CatalogId, ct2);
            if (prior is not null)
            {
                CatalogStateMachine.EnsureAllowed(CatalogStatus.Published, CatalogStatus.Superseded);
                var supersededRows = await repo.TryTransitionStatusAsync(
                    command.TenantId, prior.CatalogId, prior.Version,
                    CatalogStatus.Published, CatalogStatus.Superseded, ct2);
                if (supersededRows == 0)
                    throw new InvalidCatalogStateTransitionException(CatalogStatus.Published, CatalogStatus.Superseded);

                await auditRepo.AddAsync(BuildAudit(command, "catalog.superseded", prior.CatalogId,
                    new { superseded_by = command.CatalogId }, now), ct2);
            }

            var publishedRows = await repo.TryTransitionStatusAsync(
                command.TenantId, command.CatalogId, catalog.Version,
                CatalogStatus.Approved, CatalogStatus.Published, ct2);
            if (publishedRows == 0)
                throw new InvalidCatalogStateTransitionException(catalog.Status, CatalogStatus.Published);

            await auditRepo.AddAsync(BuildAudit(command, "catalog.published", command.CatalogId,
                new { version_label = catalog.VersionLabel }, now), ct2);
        }, ct);

        return Unit.Value;
    }

    private static AuditEvent BuildAudit(
        PublishCatalogCommand command, string eventType, string subjectRef, object metadata, DateTimeOffset now)
        => new()
        {
            TenantId = command.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = command.Actor,
            SubjectRef = subjectRef,
            CorrelationId = command.CatalogId,
            OperationId = null,
            Metadata = JsonSerializer.Serialize(metadata),
            OccurredAt = now,
        };
}
