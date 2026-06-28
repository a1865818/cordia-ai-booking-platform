using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a caller attempts a state-machine edge that is not in the §6.1 allowed-edge set
/// (spec 10 §6.1). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class InvalidNotificationStateTransitionException : DomainException
{
    /// <summary>The state the notification was in when the illegal transition was attempted.</summary>
    public NotificationState From { get; }

    /// <summary>The target state that is not a defined edge from <see cref="From"/>.</summary>
    public NotificationState To { get; }

    public InvalidNotificationStateTransitionException(NotificationState from, NotificationState to)
        : base($"Transition {from} -> {to} is not a defined notification state-machine edge.")
    {
        From = from;
        To = to;
    }
}
