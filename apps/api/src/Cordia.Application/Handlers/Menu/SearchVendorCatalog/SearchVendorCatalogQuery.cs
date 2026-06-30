using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.SearchVendorCatalog;

/// <summary>
/// Token-bounded result row for catalog search (spec 11 §7 / §21.2): name,
/// description, and price only — never draft-only fields (extraction_confidence /
/// source_location are excluded by construction, Inv-16).
/// </summary>
public sealed record CatalogItemSummary(
    string ItemId,
    string Name,
    string Description,
    long PriceMinor,
    string Currency);

/// <summary>
/// Paged search over the <b>published</b> vendor catalog (spec 11 §5.2, §7). Backs the
/// foreground <c>search_vendor_catalog</c> tool. Tenant scope is enforced by the
/// DbContext global query filter (Inv-13).
/// </summary>
public sealed record SearchVendorCatalogQuery(string? Query, int Skip, int Take)
    : IRequest<IReadOnlyList<CatalogItemSummary>>;

/// <summary>Returns published catalog items matching the query, mapped to token-bounded summaries.</summary>
public sealed class SearchVendorCatalogQueryHandler(IVendorCatalogRepository repo)
    : IRequestHandler<SearchVendorCatalogQuery, IReadOnlyList<CatalogItemSummary>>
{
    public async Task<IReadOnlyList<CatalogItemSummary>> Handle(
        SearchVendorCatalogQuery request, CancellationToken ct)
    {
        var items = await repo.SearchPublishedItemsAsync(request.Query, request.Skip, request.Take, ct);
        return items
            .Select(i => new CatalogItemSummary(i.ItemId, i.Name, i.Description, i.PriceMinor, i.Currency))
            .ToList();
    }
}
