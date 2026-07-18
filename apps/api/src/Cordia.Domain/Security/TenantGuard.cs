using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Security;

/// <summary>
/// Single fail-closed tenant-scope guard (spec 14 §24.1, Inv-11). Controllers call
/// <see cref="RequireTenant"/> instead of hand-rolling the X-Tenant-Id check; the thrown
/// <see cref="TenantScopeMissingException"/> is mapped to the same 400 "Missing tenant"
/// problem response by the GlobalExceptionHandler.
/// </summary>
public static class TenantGuard
{
    /// <summary>Returns the non-empty tenant id or throws <see cref="TenantScopeMissingException"/>.</summary>
    public static string RequireTenant(ITenantContext tenant)
        => string.IsNullOrWhiteSpace(tenant.TenantId)
            ? throw new TenantScopeMissingException()
            : tenant.TenantId;
}
