using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Menu.GetCatalogItem;

/// <summary>
/// Full published-item detail (spec 11 §4.2, §7). Carries the §4.2 rich fields
/// (modifiers / availability_window / image_ref, as stored jsonb/text) and the
/// menu/allergen metadata blobs, but never the draft-only extraction fields (Inv-16).
/// </summary>
public sealed record CatalogItemDetail(
    string ItemId,
    string Name,
    string Description,
    long PriceMinor,
    string Currency,
    string? Category,
    string? Modifiers,
    string? AvailabilityWindow,
    string? ImageRef,
    string? MenuMetadata,
    string? AllergenInfo,
    int? DurationMinutes = null);

/// <summary>
/// Single-item lookup over the <b>published</b> catalog (spec 11 §5.2, §7). Backs the
/// foreground <c>get_catalog_item</c> tool. Returns <c>null</c> when the item is not
/// found or not published (caller maps null → 404). Tenant scope via the global query
/// filter (Inv-13).
/// </summary>
public sealed record GetCatalogItemQuery(string ItemId) : IRequest<CatalogItemDetail?>;

/// <summary>Returns the published item detail, or <c>null</c> when absent/unpublished.</summary>
public sealed class GetCatalogItemQueryHandler(IVendorCatalogRepository repo)
    : IRequestHandler<GetCatalogItemQuery, CatalogItemDetail?>
{
    public async Task<CatalogItemDetail?> Handle(GetCatalogItemQuery request, CancellationToken ct)
    {
        var item = await repo.GetPublishedItemAsync(request.ItemId, ct);
        if (item is null)
            return null;

        return new CatalogItemDetail(
            item.ItemId,
            item.Name,
            item.Description,
            item.PriceMinor,
            item.Currency,
            item.Category,
            item.Modifiers,
            item.AvailabilityWindow,
            item.ImageRef,
            item.MenuMetadata,
            item.AllergenInfo,
            item.DurationMinutes);
    }
}
