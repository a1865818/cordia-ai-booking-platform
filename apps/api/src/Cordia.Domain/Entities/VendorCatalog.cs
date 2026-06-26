using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Vendor catalog version row. PK (tenant_id, catalog_id).
/// </summary>
public class VendorCatalog : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public string VersionLabel { get; set; } = string.Empty;

    public CatalogStatus Status { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public string SourceHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
