using Cordia.Domain.Enums;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a caller attempts a waitlist-offer state-machine edge that is not in the §4.4
/// allowed-edge set (spec 11 §4.4, §5.1). Maps to HTTP 409 Conflict.
/// </summary>
public sealed class InvalidWaitlistOfferTransitionException : DomainException
{
    /// <summary>The status the offer was in when the illegal transition was attempted.</summary>
    public WaitlistOfferStatus From { get; }

    /// <summary>The target status that is not a defined edge from <see cref="From"/>.</summary>
    public WaitlistOfferStatus To { get; }

    public InvalidWaitlistOfferTransitionException(WaitlistOfferStatus from, WaitlistOfferStatus to)
        : base($"Transition {from} -> {to} is not a defined waitlist-offer state-machine edge.")
    {
        From = from;
        To = to;
    }
}
