namespace Cordia.Infrastructure.Services;

/// <summary>
/// Options for minted web-origin operator sessions. Bound from the
/// "OperatorSession" configuration section. Consumed by the login handler via
/// <c>IOptions&lt;OperatorSessionOptions&gt;</c> (same layer-legal pattern as
/// <c>TenantContextOptions</c>).
/// </summary>
public sealed class OperatorSessionOptions
{
    public const string SectionName = "OperatorSession";

    /// <summary>
    /// Lifetime of a minted operator session (<c>expires_at = issued_at + TtlSeconds</c>).
    /// Default 28800s (8 hours) — a console working day, not the minutes-scale channel
    /// TenantContext TTL.
    /// </summary>
    public int TtlSeconds { get; set; } = 28800;
}
