using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;

namespace Cordia.Api.Middleware;

/// <summary>
/// The API-side trust boundary for a presented signed context (spec-15 Phase 4 + G1 web-origin).
/// When an inbound request carries <c>X-Tenant-Context</c>, the unverified JWS header <c>typ</c>
/// is peeked and dispatched:
///
/// <list type="bullet">
/// <item><c>opctx+jws</c> — <see cref="IOperatorSessionSigner.Verify"/>, then set
/// <see cref="TenantContext.TenantId"/> AND <see cref="ActorContext"/> (actor id/role).
/// Operator sessions are rare vs channel traffic, so this path also loads the
/// <c>operator_sessions</c> row by session id. Missing or revoked → 401.
/// <b>Exception:</b> <c>POST /api/auth/logout</c> skips the DB revocation check so a second
/// logout of a revoked-but-unexpired token still returns 204. JWS expiry is always enforced.
/// Forged-after-logout tokens therefore fail on every other request as soon as
/// <c>revoked_at</c> is set; they would otherwise work until <c>expires_at</c>.</item>
/// <item><c>tenantctx+jws</c> (or any other typ) — existing <see cref="ITenantContextSigner.Verify"/>.
/// Sets tenant only; does NOT populate <see cref="ActorContext"/>.</item>
/// <item>No header — pass through (endpoints fail closed at <c>TenantGuard</c>).</item>
/// </list>
///
/// <para>Runs AFTER <c>CorrelationMiddleware</c> and BEFORE the <c>X-Tenant-Id</c> shim.
/// Verification failure throws <see cref="TenantContextVerificationException"/> → HTTP 401.</para>
/// </summary>
public sealed class TenantContextVerificationMiddleware(RequestDelegate next)
{
    /// <summary>Request header carrying the signed compact-JWS context.</summary>
    public const string HeaderName = "X-Tenant-Context";

    private const string OperatorTyp = "opctx+jws";
    private const string LogoutPath = "/api/auth/logout";

    /// <summary>Verifies an inbound signed context (when present) and folds its tenant (and actor, for opctx) into the request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextSigner tenantSigner,
        IOperatorSessionSigner operatorSigner,
        IOperatorSessionRepository sessions,
        TenantContext tenantContext,
        ActorContext actorContext,
        ICorrelationContext correlation)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var jws = values.ToString();
            if (!string.IsNullOrWhiteSpace(jws))
            {
                if (PeekTyp(jws) == OperatorTyp)
                    await BindOperatorSessionAsync(
                        jws, context, operatorSigner, sessions, tenantContext, actorContext, correlation);
                else
                    BindTenantContext(jws, tenantSigner, tenantContext, correlation);
            }
        }

        await next(context);
    }

    private static void BindTenantContext(
        string jws,
        ITenantContextSigner signer,
        TenantContext tenantContext,
        ICorrelationContext correlation)
    {
        var claims = signer.Verify(jws);
        StampTenant(tenantContext, claims.TenantId);
        correlation.Set(correlation.Current with { TenantId = claims.TenantId });
    }

    /// <summary>
    /// A signature proves who issued a context, not that its claims are usable. A blank tenant
    /// would make <see cref="TenantContext.SetTenant"/> throw <see cref="ArgumentException"/>, which
    /// nothing maps, so the caller got a 500 for what is a context that cannot be honoured. It is
    /// refused as a verification failure — the same 401 as any other unusable context.
    /// </summary>
    private static void StampTenant(TenantContext tenantContext, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new TenantContextVerificationException("Signed context carries no tenant.");

        tenantContext.SetTenant(tenantId);
    }

    private static async Task BindOperatorSessionAsync(
        string jws,
        HttpContext http,
        IOperatorSessionSigner signer,
        IOperatorSessionRepository sessions,
        TenantContext tenantContext,
        ActorContext actorContext,
        ICorrelationContext correlation)
    {
        var claims = signer.Verify(jws);

        // Set tenant BEFORE the session lookup so the EF query filter matches the claim.
        StampTenant(tenantContext, claims.TenantId);
        correlation.Set(correlation.Current with { TenantId = claims.TenantId });

        // Skip DB revocation on logout so a second POST /api/auth/logout of a revoked-but-unexpired
        // token still returns 204. Every other path (including GET /api/auth/session) fails closed.
        var skipRevocationCheck = HttpMethods.IsPost(http.Request.Method)
            && http.Request.Path.StartsWithSegments(LogoutPath, StringComparison.OrdinalIgnoreCase);

        if (!skipRevocationCheck)
        {
            var session = await sessions.GetAsync(claims.TenantId, claims.SessionId, http.RequestAborted);
            if (session is null || session.RevokedAt is not null)
                throw new TenantContextVerificationException("Operator session is not active.");
        }

        actorContext.ActorId = claims.ActorId;
        actorContext.ActorRole = claims.ActorRole;
    }

    /// <summary>
    /// Peeks the unverified JWS header <c>typ</c> (first compact-JWS segment). Returns null when
    /// the header cannot be decoded — the tenant-context verifier then fail-closes as malformed.
    /// </summary>
    internal static string? PeekTyp(string jws)
    {
        var dot = jws.IndexOf('.');
        if (dot <= 0)
            return null;

        try
        {
            var headerJson = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(jws.AsSpan(0, dot)));
            using var doc = JsonDocument.Parse(headerJson);
            return doc.RootElement.TryGetProperty("typ", out var typ) && typ.ValueKind == JsonValueKind.String
                ? typ.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
