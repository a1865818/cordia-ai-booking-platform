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

    /// <summary>jsonb — menu metadata blob; null when not provided.</summary>
    public string? MenuMetadata { get; set; }

    /// <summary>jsonb — allergen information blob; null when not provided.</summary>
    public string? AllergenInfo { get; set; }

    /// <summary>Draft-only: extraction-agent confidence (§3); null once owner-confirmed. Never customer-facing as fact (Inv-16).</summary>
    public double? ExtractionConfidence { get; set; }

    /// <summary>Draft-only: source location within the artifact (§3); never customer-facing.</summary>
    public string? SourceLocation { get; set; }
}
