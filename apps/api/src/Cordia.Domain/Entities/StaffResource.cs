using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Named staff member a tenant can assign to work. PK (tenant_id, resource_id).
/// Distinct from <see cref="TenantResource"/>, which names bookable services and locations.
/// </summary>
public class StaffResource : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Role { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
