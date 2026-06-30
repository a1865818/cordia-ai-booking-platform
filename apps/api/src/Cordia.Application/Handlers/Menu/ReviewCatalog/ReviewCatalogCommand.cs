using Cordia.Domain.Menu;
using MediatR;

namespace Cordia.Application.Handlers.Menu.ReviewCatalog;

/// <summary>
/// Owner opens a draft catalog for review (spec 11 §5.2): <c>draft_ready → in_review</c>, optionally
/// applying corrections to flagged draft items in the same transaction. Synchronous HTTP command.
/// </summary>
public sealed record ReviewCatalogCommand(
    string TenantId,
    string CatalogId,
    string Actor,
    IReadOnlyList<CatalogItemEdit> Edits) : IRequest<Unit>;
