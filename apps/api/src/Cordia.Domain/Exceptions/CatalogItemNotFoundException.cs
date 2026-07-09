namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an owner item edit targets a catalog item id that does not exist in the editable
/// (<c>in_review</c>) catalog (spec 11 §5.2) — e.g. a stale or malformed id. Surfacing this prevents an
/// edit from silently no-op'ing (the "correct the uncertain price before publish" workflow must not
/// fail silently). Maps to HTTP 404 Not Found.
/// </summary>
public sealed class CatalogItemNotFoundException : DomainException
{
    /// <summary>The item id that matched no editable row.</summary>
    public string ItemId { get; }

    public CatalogItemNotFoundException(string itemId)
        : base($"Catalog item '{itemId}' was not found in an editable catalog.")
    {
        ItemId = itemId;
    }
}
