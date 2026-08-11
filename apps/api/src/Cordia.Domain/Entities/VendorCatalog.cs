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

    /// <summary>
    /// When the row last ENTERED its current <see cref="Status"/>.
    ///
    /// <para><b>Why not <see cref="CreatedAt"/>.</b> Staleness is "how long has this been stuck
    /// in <c>scanning</c>/<c>extracting</c>", and since the monitor now settles stale rows into
    /// the terminal <c>extract_failed</c>, getting that wrong destroys work. A catalog whose
    /// ingestion message sat in a queue backlog for longer than the threshold before starting is
    /// perfectly healthy, but measured from creation it is already past due the instant it begins
    /// — so it would be failed on the monitor's very next tick, and the extractor still running
    /// would then lose its own version-guarded transition and throw the results away.</para>
    ///
    /// <para>Every status write must set this, or the row's clock silently freezes at the
    /// previous transition and the same over-eager failure returns.</para>
    /// </summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
