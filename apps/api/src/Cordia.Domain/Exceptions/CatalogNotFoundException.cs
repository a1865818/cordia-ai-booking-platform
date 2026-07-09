namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an owner lifecycle action (review/approve/publish) targets a vendor catalog
/// that does not exist for the current tenant (spec 11 §5.2). Maps to HTTP 404 Not Found.
/// </summary>
public sealed class CatalogNotFoundException : DomainException
{
    /// <summary>The catalog id that was not found for the current tenant.</summary>
    public string CatalogId { get; }

    public CatalogNotFoundException(string catalogId)
        : base($"Vendor catalog '{catalogId}' was not found.")
    {
        CatalogId = catalogId;
    }
}
