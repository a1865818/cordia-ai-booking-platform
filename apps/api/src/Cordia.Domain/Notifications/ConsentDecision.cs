namespace Cordia.Domain.Notifications;

/// <summary>Outcome of a consent check for a recipient + channel pair.</summary>
public enum ConsentDecision
{
    Allowed,
    Suppressed,
    Withdrawn,
}
