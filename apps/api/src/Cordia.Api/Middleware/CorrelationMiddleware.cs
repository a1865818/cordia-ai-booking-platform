using System.Diagnostics;
using System.Text.RegularExpressions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Api.Middleware;

/// <summary>
/// Ingress correlation minter (§25.1). Echoes an inbound <c>X-Correlation-Id</c> when present and
/// non-empty, otherwise mints a fresh <c>corr_</c> id once — NEVER re-minting mid-flow. Reads an
/// optional <c>X-Causation-Id</c>, establishes the ambient
/// <see cref="AmbientCorrelationContext"/> via <see cref="ICorrelationContext"/>, stamps the ids
/// onto the current <see cref="Activity"/>, and echoes <c>X-Correlation-Id</c> back on the response.
///
/// <para>Runs BEFORE <see cref="TenantResolutionMiddleware"/> so the correlation context exists
/// (and tenant, once resolved, is folded in by <c>CorrelationEnrichmentBehavior</c>) before any
/// downstream work or logging happens.</para>
/// </summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next)
{
    /// <summary>Request/response header carrying the correlation id.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Optional inbound header carrying the causation id (lineage).</summary>
    public const string CausationHeader = "X-Causation-Id";

    // Allowlist for inbound ids: bounded length, no control chars / newlines / delimiters.
    // Echoing a raw client-supplied header into logs + the response would be a log-injection /
    // response-splitting vector — only ids matching this safe shape are honoured; anything else is
    // treated as absent (correlation is freshly minted; causation is dropped).
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdRegex();

    private static string? SafeInboundId(HttpContext context, string headerName) =>
        context.Request.Headers.TryGetValue(headerName, out var values)
        && SafeIdRegex().IsMatch(values.ToString())
            ? values.ToString()
            : null;

    /// <summary>Mints/echoes the correlation context for the request.</summary>
    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlation)
    {
        // Echo a VALID inbound correlation id (never re-mint, §25.1); otherwise mint a fresh one.
        var correlationId = SafeInboundId(context, CorrelationHeader) ?? $"corr_{Guid.NewGuid():N}";

        // Causation is internal lineage metadata — read only a safe inbound value, never echoed back.
        string? causationId = SafeInboundId(context, CausationHeader);

        correlation.Set(new AmbientCorrelationContext(
            TenantId: string.Empty, // resolved later by TenantResolutionMiddleware / folded in by the behaviour
            CorrelationId: correlationId,
            CausationId: causationId));

        var activity = Activity.Current;
        activity?.SetTag("correlation_id", correlationId);
        if (causationId is not null)
            activity?.SetTag("causation_id", causationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }
}
