using Cordia.Infrastructure.Services;

namespace Cordia.Api.Middleware;

/// <summary>
/// DEV/TEST-ONLY <c>X-Tenant-Id</c> tenant shim (Inv-13: the authoritative tenant comes from a signed
/// context verified by <see cref="TenantContextVerificationMiddleware"/>, NOT from arbitrary client
/// input — this header is dev/test scaffolding). Reads the <c>X-Tenant-Id</c> request header and sets
/// the request-scoped <see cref="TenantContext.TenantId"/>, but ONLY when no tenant has already been
/// resolved — so it can never override a verified signed context. Does not hard-fail when the header
/// is absent; endpoints that require a tenant validate it themselves (fail closed at
/// <c>TenantGuard.RequireTenant</c>).
///
/// <para>Registered AFTER <see cref="TenantContextVerificationMiddleware"/> in <c>Program.cs</c>: the
/// verification middleware runs first and, on a valid <c>X-Tenant-Context</c>, sets the tenant; this
/// shim then observes a non-empty <see cref="TenantContext.TenantId"/> and does nothing.</para>
///
/// <para><b>Registration is conditional.</b> <c>Program.cs</c> adds this middleware only when
/// <c>Tenancy:AllowHeaderShim</c> is true, which it defaults to only in Development. Ordering alone
/// is not a security control: where this runs and no signed context is present, a caller names the
/// tenant whose data they receive. With the shim off, a request carrying no verified context fails
/// closed at <c>TenantGuard.RequireTenant</c> rather than being served another tenant's rows.</para>
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>The HTTP request header name used to carry the tenant identifier.</summary>
    public const string HeaderName = "X-Tenant-Id";

    /// <summary>Reads <see cref="HeaderName"/> and populates the scoped <see cref="TenantContext"/>.</summary>
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        // Never override a verified signed context (Inv-13): if TenantContextVerificationMiddleware
        // already set the tenant, the dev/test X-Tenant-Id shim is a no-op.
        if (string.IsNullOrEmpty(tenantContext.TenantId)
            && context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var tenantId = values.ToString();
            if (!string.IsNullOrWhiteSpace(tenantId))
                tenantContext.SetTenant(tenantId);
        }

        await next(context);   // does NOT hard-fail when absent; endpoints that require a tenant validate it
    }
}
