using Cordia.Domain.Capacity;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Which capacity-ladder rung a tenant's external provider supports (spec 07 §6.3, #106).
/// PK (tenant_id, provider_name). No row means the §13 default: first-party.
///
/// <para>Provisioned with the provider, at onboarding — there is no API for it, because choosing a
/// rung is a statement about what a provider integration can actually do, not an operator
/// preference.</para>
/// </summary>
public class TenantCapacityPrimitive : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The booking provider the rung applies to, e.g. <c>acme_reservations</c>.</summary>
    public string ProviderName { get; set; } = string.Empty;

    public CapacityPrimitive Primitive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
