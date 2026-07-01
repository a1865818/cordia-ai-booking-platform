namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a waitlist action (cancel, etc.) targets an enrollment that does not exist for
/// the current tenant (spec 11 §4.4). Maps to HTTP 404 Not Found.
/// </summary>
public sealed class WaitlistEnrollmentNotFoundException : DomainException
{
    /// <summary>The enrollment id that was not found for the current tenant.</summary>
    public string EnrollmentId { get; }

    public WaitlistEnrollmentNotFoundException(string enrollmentId)
        : base($"Waitlist enrollment '{enrollmentId}' was not found.")
    {
        EnrollmentId = enrollmentId;
    }
}
