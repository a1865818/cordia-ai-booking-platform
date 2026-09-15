using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// Scoped implementation of <see cref="ITenantContext"/>. The tenant is stamped once per scope
/// through <see cref="SetTenant"/> and cannot be re-pointed at another tenant afterwards (#80,
/// Inv-13).
///
/// <para><b>Why a repeat of the same value is tolerated rather than refused.</b> Dead-letter replay
/// runs inside an HTTP request the verification middleware has already stamped, and then
/// <c>PostgresMessageTransport</c> stamps the envelope's tenant again in that same scope. The dead
/// letter was read through the tenant-filtered context, so the two values agree — a strictly
/// write-once setter would throw on every replay. What must never happen is a scope silently
/// switching tenants mid-flight, and that is what this refuses.</para>
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>An unstamped scope; the tenant is set later through <see cref="SetTenant"/>.</summary>
    public TenantContext() { }

    /// <summary>A scope stamped with <paramref name="tenantId"/> from the start.</summary>
    public TenantContext(string tenantId) => SetTenant(tenantId);

    public string TenantId { get; private set; } = string.Empty;

    /// <summary>
    /// Establishes the tenant for this scope. A no-op when the same tenant is already set; throws
    /// when a different tenant is already set.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">The scope already belongs to another tenant.</exception>
    public void SetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (TenantId.Length == 0)
        {
            TenantId = tenantId;
            return;
        }

        if (!string.Equals(TenantId, tenantId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This scope is already bound to a different tenant; a tenant scope cannot be re-pointed (Inv-13).");
    }
}
