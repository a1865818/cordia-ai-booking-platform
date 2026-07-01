namespace Cordia.Domain.Bookings;

/// <summary>Minimized non-sensitive disambiguator for Manage Booking discovery.</summary>
public sealed record ManageBookingCandidate(
    string BookingId,
    string BookingReference,
    string BusinessName,
    DateOnly BookingDate,
    string ApproxTime,
    int PartySize,
    string LifecycleStatus);
