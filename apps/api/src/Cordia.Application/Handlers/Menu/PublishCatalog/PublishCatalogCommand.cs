using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.Menu.PublishCatalog;

/// <summary>
/// Owner publishes an approved catalog (spec 11 §5.2): <c>approved → published</c>, and supersedes
/// the tenant's prior live published version (<c>published → superseded</c>) in the same transaction
/// so exactly one version is live. Published items are immutable. Synchronous HTTP command.
/// </summary>
public sealed record PublishCatalogCommand(
    string TenantId,
    string CatalogId,
    string Actor,
    string ReasonCode) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string ActionName => "catalog.published";

    public string TargetType => "vendor_catalog";

    public string TargetId => CatalogId;
}
