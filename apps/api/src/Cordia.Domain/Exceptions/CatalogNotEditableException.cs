using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an owner attempts to edit catalog items while the catalog is not in an editable
/// state (spec 11 §5.2). Items are editable ONLY while the catalog is <see cref="CatalogStatus.InReview"/>;
/// draft items are frozen once approved/published (immutability). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class CatalogNotEditableException : DomainException
{
    /// <summary>The catalog that could not be edited.</summary>
    public string CatalogId { get; }

    /// <summary>The status the catalog was in when the edit was attempted.</summary>
    public CatalogStatus Status { get; }

    public CatalogNotEditableException(string catalogId, CatalogStatus status)
        : base($"Catalog '{catalogId}' is not editable in status {status}; items can only be edited while in_review.")
    {
        CatalogId = catalogId;
        Status = status;
    }
}
