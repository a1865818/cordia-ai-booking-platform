namespace Cordia.Infrastructure.Services;

/// <summary>
/// Options for the spec-15 channel-gateway <c>TenantContext</c> minting. Bound from the
/// "TenantContext" configuration section. Consumed by the Application resolution handler via
/// <c>IOptions&lt;TenantContextOptions&gt;</c> (same layer-legal pattern as <c>CapacityOptions</c>).
/// </summary>
public sealed class TenantContextOptions
{
    public const string SectionName = "TenantContext";

    /// <summary>
    /// Short lifetime of a minted <c>TenantContext</c> (<c>expires_at = issued_at + TtlSeconds</c>).
    /// Deliberately minutes-scale (spec-15 OD-3 placeholder): the context must outlive setup + a
    /// brief pause but expire fast so a leaked token is useless (§7). Default 300s (5 min).
    /// </summary>
    public int TtlSeconds { get; set; } = 300;
}
