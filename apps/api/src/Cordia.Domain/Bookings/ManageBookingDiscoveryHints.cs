namespace Cordia.Domain.Bookings;

/// <summary>Caller-provided discovery hints. These identify candidates but never authorize access.</summary>
public sealed record ManageBookingDiscoveryHints(
    string? CallerId = null,
    string? PhoneE164 = null,
    string? Email = null,
    string? BookingReference = null);
