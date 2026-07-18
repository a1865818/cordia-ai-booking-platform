using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a caller attempts a pre-order saga state-machine edge that is not in the §5.3
/// allowed-edge set (forward or compensation). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class InvalidPreorderSagaTransitionException : DomainException
{
    /// <summary>The state the saga was in when the illegal transition was attempted.</summary>
    public PreorderSagaState From { get; }

    /// <summary>The target state that is not a defined edge from <see cref="From"/>.</summary>
    public PreorderSagaState To { get; }

    public InvalidPreorderSagaTransitionException(PreorderSagaState from, PreorderSagaState to)
        : base($"Transition {from} -> {to} is not a defined pre-order saga state-machine edge.")
    {
        From = from;
        To = to;
    }
}
