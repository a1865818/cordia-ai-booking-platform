namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a waitlist action (accept / decline) targets an offer that does not exist for the
/// current tenant (spec 11 §4.4, §5.1). Maps to HTTP 404 Not Found.
/// </summary>
public sealed class WaitlistOfferNotFoundException : DomainException
{
    /// <summary>The offer id that was not found for the current tenant.</summary>
    public string OfferId { get; }

    public WaitlistOfferNotFoundException(string offerId)
        : base($"Waitlist offer '{offerId}' was not found.")
    {
        OfferId = offerId;
    }
}
