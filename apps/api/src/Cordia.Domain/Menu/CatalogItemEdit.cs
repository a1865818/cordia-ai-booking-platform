namespace Cordia.Domain.Menu;

/// <summary>
/// An owner correction applied to a draft catalog item during review (spec 11 §5.2). Only the
/// non-null fields are changed; a null field leaves the existing value untouched. Edits are legal
/// ONLY while the catalog is pre-publish (draft_ready → in_review); published items are immutable.
/// </summary>
public sealed record CatalogItemEdit(
    string ItemId,
    string? Name,
    string? Description,
    long? PriceMinor);
