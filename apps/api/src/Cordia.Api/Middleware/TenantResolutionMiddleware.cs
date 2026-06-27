using Cordia.Infrastructure.Tenancy;

namespace Cordia.Api.Middleware;

/// <summary>
/// Placeholder for spec 08/15 signed-context tenant resolution (Inv-13: tenant is authoritative
/// from the signed context, NOT trusted from arbitrary client input — this header shim is
/// dev/test scaffolding until spec 08 lands). Reads the <c>X-Tenant-Id</c> request header and
/// sets the request-scoped <see cref="TenantContext.TenantId"/>. Does not hard-fail when the
/// header is absent; endpoints that require a tenant validate it themselves.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>The HTTP request header name used to carry the tenant identifier.</summary>
    public const string HeaderName = "X-Tenant-Id";

    /// <summary>Reads <see cref="HeaderName"/> and populates the scoped <see cref="TenantContext"/>.</summary>
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var tenantId = values.ToString();
            if (!string.IsNullOrWhiteSpace(tenantId))
                tenantContext.TenantId = tenantId;
        }

        await next(context);   // does NOT hard-fail when absent; endpoints that require a tenant validate it
    }
}
