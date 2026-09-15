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

    /// <summary>
    /// IANA timezone the venue's wall clock runs on, e.g. <c>Australia/Melbourne</c>.
    ///
    /// The agent needs this to mean anything by a spoken time. Without it "seven pm" was
    /// stamped 19:00Z — 5am local — so a booking landed outside every sitting and the
    /// read-back confirmed a time nobody said.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>ISO 4217 the venue prices in, e.g. <c>AUD</c>. Read aloud with menu prices.</summary>
    public string? Currency { get; set; }

    /// <summary>jsonb of 7 day objects: <c>[{ "d": "Monday", "open": "09:00", "close": "18:00", "on": true }, ...]</c>.</summary>
    public string? HoursJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
