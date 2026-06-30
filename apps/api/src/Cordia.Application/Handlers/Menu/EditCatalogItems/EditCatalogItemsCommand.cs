using Cordia.Domain.Menu;
using MediatR;

namespace Cordia.Application.Handlers.Menu.EditCatalogItems;

/// <summary>
/// Owner applies corrections to draft catalog items while the catalog is <c>in_review</c>
/// (spec 11 §5.2), WITHOUT changing the catalog status. Enables multiple edit rounds between
/// review and approval. Synchronous HTTP command.
/// </summary>
public sealed record EditCatalogItemsCommand(
    string TenantId,
    string CatalogId,
    string Actor,
    IReadOnlyList<CatalogItemEdit> Edits) : IRequest<Unit>;
