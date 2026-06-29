using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a caller attempts a state-machine edge that is not in the §11 allowed-edge set
/// (spec 06 §11). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class InvalidStateTransitionException : DomainException
{
    /// <summary>The status the operation was in when the illegal transition was attempted.</summary>
    public OperationStatus From { get; }

    /// <summary>The target status that is not a defined edge from <see cref="From"/>.</summary>
    public OperationStatus To { get; }

    public InvalidStateTransitionException(OperationStatus from, OperationStatus to)
        : base($"Transition {from} -> {to} is not a defined operation state-machine edge.")
    {
        From = from;
        To = to;
    }
}
