using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;

namespace Cordia.Api.Middleware;

/// <summary>
/// The API-side trust boundary for a presented signed <c>TenantContext</c> (spec-15 Phase 4, Inv-13)
/// — the mirror of the langgraph <c>resolve_tenant</c> node. When an inbound request carries the
/// <c>X-Tenant-Context</c> header, its compact HS256 JWS is verified via
/// <see cref="ITenantContextSigner"/>; on success the verified <c>tenant_id</c> becomes authoritative
/// for the request and is folded into the ambient correlation context BEFORE any handler touches data.
///
/// <para>Runs AFTER <c>CorrelationMiddleware</c> (so the correlation id already exists) and BEFORE the
/// <c>TenantResolutionMiddleware</c> <c>X-Tenant-Id</c> dev/test shim — the shim is gated so it can
/// never override a tenant this middleware verified.</para>
///
/// <para>On verification failure <see cref="ITenantContextSigner.Verify"/> throws
/// <see cref="Cordia.Domain.Exceptions.TenantContextVerificationException"/>, which
/// <c>UseExceptionHandler</c> (first in the pipeline) maps to HTTP 401 via the GlobalExceptionHandler.
/// When no header is present this middleware does nothing — endpoints that require a tenant then fail
/// closed at <c>TenantGuard.RequireTenant</c> (Inv-11).</para>
/// </summary>
public sealed class TenantContextVerificationMiddleware(RequestDelegate next)
{
    /// <summary>Request header carrying the signed compact-JWS <c>TenantContext</c>.</summary>
    public const string HeaderName = "X-Tenant-Context";

    /// <summary>Verifies an inbound signed context (when present) and folds its tenant into the request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextSigner signer,
        TenantContext tenantContext,
        ICorrelationContext correlation)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var jws = values.ToString();
            if (!string.IsNullOrWhiteSpace(jws))
            {
                // Inv-13: a tenant identity is accepted ONLY from a context that verifies here. Verify
                // throws TenantContextVerificationException on ANY failure (unknown/retired kid, bad
                // signature, expired, future issued_at beyond skew, wrong typ, malformed) → 401. Never
                // logs the token or the signing key.
                var claims = signer.Verify(jws);

                // Authoritative tenant for the request scope, set BEFORE the handler runs so the EF
                // global query filter and TenantGuard both see the verified identity.
                tenantContext.TenantId = claims.TenantId;

                // Fold the verified tenant into the ambient correlation context so every downstream
                // log / trace / audit line carries it (§25.1). CorrelationMiddleware already set the
                // correlation id; we only add the tenant, preserving the rest.
                correlation.Set(correlation.Current with { TenantId = claims.TenantId });
            }
        }

        await next(context);
    }
}
