using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Individual item within a vendor catalog. PK (tenant_id, item_id).
/// FK (tenant_id, catalog_id) → vendor_catalogs ON DELETE RESTRICT.
/// MenuMetadata and AllergenInfo are stored as jsonb.
/// </summary>
public class VendorCatalogItem : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public long PriceMinor { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Optional menu category (§4.2); backs the foreground <c>category</c> search filter.</summary>
    public string? Category { get; set; }

    /// <summary>jsonb — serialized list of <c>CatalogModifier</c> (§4.2, §21.3); null when none.</summary>
    public string? Modifiers { get; set; }

    /// <summary>jsonb — serialized <c>AvailabilityWindow</c> (§4.2); null ⇒ always available.</summary>
    public string? AvailabilityWindow { get; set; }

    /// <summary>Optional opaque image reference (§4.2), e.g. an object-store key; null when none.</summary>
    public string? ImageRef { get; set; }

    /// <summary>jsonb — menu metadata blob; null when not provided.</summary>
    public string? MenuMetadata { get; set; }

    /// <summary>jsonb — allergen information blob; null when not provided.</summary>
    public string? AllergenInfo { get; set; }

    /// <summary>Draft-only: extraction-agent confidence (§3); null once owner-confirmed. Never customer-facing as fact (Inv-16).</summary>
    public double? ExtractionConfidence { get; set; }

    /// <summary>Draft-only: source location within the artifact (§3); never customer-facing.</summary>
    public string? SourceLocation { get; set; }

    /// <summary>Optional service duration in minutes; null when the item is not time-boxed.</summary>
    public int? DurationMinutes { get; set; }
}
