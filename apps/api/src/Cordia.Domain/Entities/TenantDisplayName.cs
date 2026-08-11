using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// The tenant's own display name — the venue as a guest would say it.
///
/// <para>Added because <c>business_name</c> had no source anywhere in the platform: there is no
/// tenant profile entity, and <c>TenantContextClaims</c> (spec 15) carries only routing and
/// policy identifiers, nothing guest-facing. Without it,
/// <c>BookingReadRepository.ToProjection</c> could never emit a <c>ManageBookingCandidate</c>,
/// so <c>GET /api/bookings?phone_e164=</c> returned nothing for every real booking — see
/// docs §12 gap 11 and §13 B2.</para>
///
/// <para>Deliberately minimal. This is NOT a general tenant-profile table: it holds the one
/// field two ratified contracts already require (spec 08's customer-facing
/// <c>business_name</c>, and the console shell showing a venue name rather than an id).
/// Anything richer — addresses, branding, trading hours — is spec 12's to design, and guessing
/// at that shape here would be exactly the invented table this change set out to avoid.</para>
/// </summary>
public class TenantDisplayName : ITenantScoped
{
    /// <summary>PK. One display name per tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Shown to guests via <c>ManageBookingCandidate.business_name</c>, so it is a name, never
    /// an identifier. Absent for a tenant that has not set one — and absent is a real answer:
    /// nothing derives a name from the tenant id, which would show a guest a slug.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
