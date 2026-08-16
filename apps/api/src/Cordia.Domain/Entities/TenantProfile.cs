using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Tenant business profile (hours, waitlist auto-book). PK tenant_id.
/// Separate from <see cref="TenantDisplayName"/>, which holds only the guest-facing venue name.
/// </summary>
public class TenantProfile : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>salon | restaurant | medical | fitness | cleaning | lawn | repair | other.</summary>
    public string? BusinessType { get; set; }

    public bool WaitlistAutoBook { get; set; }

    /// <summary>jsonb of 7 day objects: <c>[{ "d": "Monday", "open": "09:00", "close": "18:00", "on": true }, ...]</c>.</summary>
    public string? HoursJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
