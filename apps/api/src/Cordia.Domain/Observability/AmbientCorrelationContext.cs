namespace Cordia.Domain.Observability;

/// <summary>
/// Immutable correlation context carried alongside a unit of work (§25.1, §4.1).
///
/// <para>This is the .NET equivalent of the Python <c>contextvar</c> the spec mandates:
/// a single <c>correlation_id</c> minted at channel ingress plus the <c>causation_id</c>
/// lineage chain, threaded through OTel <c>Activity</c> tags and Serilog log lines so that
/// one <c>corr_…</c> links ingress → trace → tool → intent → operation → outbox → inbox →
/// broker → attempt → notification (§25.1 checklist).</para>
///
/// <para>Named <c>AmbientCorrelationContext</c> to avoid clashing with any generated
/// <c>Cordia.Domain.Generated.CorrelationContext</c> wire type.</para>
/// </summary>
/// <param name="TenantId">Authoritative tenant identifier for the unit of work.</param>
/// <param name="CorrelationId">The single id minted once at channel ingress; never re-minted mid-flow (§25.1).</param>
/// <param name="CausationId">The causing <c>message_id</c>/span id that produced this work (§25.1 lineage).</param>
/// <param name="SessionId">Optional conversation/session identifier.</param>
/// <param name="TurnId">Optional per-turn identifier.</param>
/// <param name="OperationId">Optional booking-operation identifier.</param>
public sealed record AmbientCorrelationContext(
    string TenantId,
    string CorrelationId,
    string? CausationId = null,
    string? SessionId = null,
    string? TurnId = null,
    string? OperationId = null)
{
    /// <summary>
    /// The empty/unset context — used as the default before ingress mints a correlation id.
    /// Both <see cref="TenantId"/> and <see cref="CorrelationId"/> are empty strings, which the
    /// enrichment behaviour treats as "missing" (§8 fail-open uncorrelated-record path).
    /// </summary>
    public static AmbientCorrelationContext Empty { get; } =
        new(TenantId: string.Empty, CorrelationId: string.Empty);
}
