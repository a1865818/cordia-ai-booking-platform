namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an owner attempts to publish a catalog that contains no items (spec 11 §5.2).
///
/// <para>The <c>approved → published</c> edge remains legal in the abstract — this refuses the
/// instance. Publishing an itemless catalog supersedes the tenant's live published version, after
/// which the foreground reads (<c>search_vendor_catalog</c> / <c>get_catalog_item</c>) return
/// nothing and the agent can no longer offer any menu item. There is no in-product way back:
/// <c>CatalogItemEdit</c> can change existing rows but never add one, so recovery is a fresh
/// upload — the same route <c>extract_failed</c> takes. Maps to HTTP 409 Conflict.</para>
/// </summary>
public sealed class CatalogHasNoItemsException : DomainException
{
    /// <summary>The catalog that could not be published.</summary>
    public string CatalogId { get; }

    public CatalogHasNoItemsException(string catalogId)
        : base($"Catalog '{catalogId}' has no items and cannot be published; publishing it would retire the live menu and leave nothing to offer.")
    {
        CatalogId = catalogId;
    }
}
