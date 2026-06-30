using MediatR;

namespace Cordia.Application.Handlers.Menu.ApproveCatalog;

/// <summary>
/// Owner approves a reviewed catalog (spec 11 §5.2): <c>in_review → approved</c>, recording the
/// approver and approval timestamp. Synchronous HTTP command.
/// </summary>
public sealed record ApproveCatalogCommand(
    string TenantId,
    string CatalogId,
    string Actor) : IRequest<Unit>;
