namespace Cordia.Domain.Exceptions;

/// <summary>
/// Raised when a manage-booking mutation or validation was based on an older
/// booking version than the authoritative current version.
/// </summary>
public sealed class StaleBookingVersionException : DomainException
{
    public StaleBookingVersionException(string bookingId, long expectedVersion, long currentVersion)
        : base($"Booking '{bookingId}' changed from version {expectedVersion} to {currentVersion}. Reload before retrying.")
    {
        BookingId = bookingId;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    public string BookingId { get; }

    public long ExpectedVersion { get; }

    public long CurrentVersion { get; }
}
