using System.Text.Json;
using Cordia.Application.Handlers.Menu.GetCatalogItem;
using Cordia.Application.Handlers.Menu.SearchVendorCatalog;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Customer-facing read endpoints over the <b>published</b> vendor catalog (spec 11 §5.2, §7).
/// Backs the foreground tools <c>search_vendor_catalog</c> / <c>get_catalog_item</c> — the routes
/// and query params mirror <c>apps/langgraph/.../tools/foreground.py</c> verbatim:
///
/// GET /api/catalog/items?query=&amp;category=&amp;max_results=  → 200 (published-only, tenant-scoped)
/// GET /api/catalog/items/{itemId}                          → 200 | 404
///
/// Thin by design: resolves the tenant (X-Tenant-Id), sends a MediatR query, and maps the result.
/// Only published items are visible (Inv-16); tenant scope is enforced by the DbContext global
/// query filter (Inv-13). Draft-only fields (extraction_confidence / source_location) are never
/// returned. The wire shape is snake_case to match the §4.2 catalog contract.
/// </summary>
[ApiController]
[Route("api/catalog")]
public class CatalogController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Default page size when <c>max_results</c> is absent (token budget, §7).</summary>
    private const int DefaultMaxResults = 20;

    /// <summary>Searches published catalog items by keyword + optional category. Returns a token-bounded page.</summary>
    [HttpGet("items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "category")] string? category,
        [FromQuery(Name = "max_results")] int? maxResults,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        // Clamp the page size into the validator's [1, MaxPageSize] bound (the foreground tool sends
        // max_results, never skip/offset — paging is a single bounded page).
        var take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, SearchVendorCatalogValidator.MaxPageSize);

        var items = await mediator.Send(new SearchVendorCatalogQuery(query, category, 0, take), ct);

        return Ok(items.Select(i => new
        {
            item_id = i.ItemId,
            name = i.Name,
            description = i.Description,
            price_minor = i.PriceMinor,
            currency = i.Currency,
            category = i.Category,
        }));
    }

    /// <summary>Returns a single published item's detail, or 404 when absent / unpublished.</summary>
    [HttpGet("items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItem(string itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var item = await mediator.Send(new GetCatalogItemQuery(itemId), ct);
        if (item is null)
            return NotFound();

        return Ok(new
        {
            item_id = item.ItemId,
            name = item.Name,
            description = item.Description,
            price_minor = item.PriceMinor,
            currency = item.Currency,
            category = item.Category,
            // §4.2 rich fields: modifiers/availability_window are stored jsonb — emit them as nested
            // JSON (array/object), not as a double-encoded string. image_ref is a plain reference.
            modifiers = AsJson(item.Modifiers),
            availability_window = AsJson(item.AvailabilityWindow),
            image_ref = item.ImageRef,
            menu_metadata = item.MenuMetadata,
            allergen_info = item.AllergenInfo,
        });
    }

    /// <summary>Parses a stored jsonb string into a JSON element so it serializes as nested JSON, not a string.</summary>
    private static JsonElement? AsJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
