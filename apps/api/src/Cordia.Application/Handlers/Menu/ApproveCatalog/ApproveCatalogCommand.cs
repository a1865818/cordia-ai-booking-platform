using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.Menu.ApproveCatalog;

/// <summary>
/// Owner approves a reviewed catalog (spec 11 §5.2): <c>in_review → approved</c>, recording the
/// approver and approval timestamp. Synchronous HTTP command. An owner action on the spec-14
/// operator spine (§24.5): actor + reason code are mandatory and audited.
/// </summary>
public sealed record ApproveCatalogCommand(
    string TenantId,
    string CatalogId,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "catalog.approved";

    public string TargetType => "vendor_catalog";

    public string TargetId => CatalogId;
}
