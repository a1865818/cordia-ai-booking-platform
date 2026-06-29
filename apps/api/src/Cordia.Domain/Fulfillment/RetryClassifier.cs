using Cordia.Domain.Enums;

namespace Cordia.Domain.Fulfillment;

/// <summary>
/// Pure stateless classifier that maps a <see cref="ProviderResult"/> (or an unexpected exception)
/// to a <see cref="RetryDecision"/> (spec 06 §15.1, §6.3 Classify step).
/// Register as a singleton.
/// </summary>
public sealed class RetryClassifier
{
    /// <summary>
    /// Maps a non-success <see cref="ProviderResult"/> to a <see cref="RetryDecision"/>.
    /// </summary>
    /// <param name="result">The provider result to classify. Must not be a success kind.</param>
    /// <returns>The retry decision.</returns>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="result"/> has kind <see cref="ProviderResultKind.Created"/> or
    ///   <see cref="ProviderResultKind.AlreadyExists"/> — the classifier must not be called for successful outcomes.
    /// </exception>
    public RetryDecision Classify(ProviderResult result) => result.Kind switch
    {
        ProviderResultKind.NoCapacity => new RetryDecision(RetryClassification.NoCapacity, RetryAction.NoCapacity),
        ProviderResultKind.Rejected => new RetryDecision(RetryClassification.Validation, RetryAction.Terminal),
        ProviderResultKind.PaymentActionRequired => new RetryDecision(RetryClassification.PaymentAction, RetryAction.AwaitUser),
        // System-credential failure (our key/account is bad) → ManualReview. NOT the customer-facing
        // "auth required" path; that is PaymentActionRequired → AwaitUser (§6.3).
        ProviderResultKind.AuthError => new RetryDecision(RetryClassification.Authentication, RetryAction.ManualReview),
        ProviderResultKind.Transient => new RetryDecision(RetryClassification.Transient, RetryAction.ScheduleRetry),
        ProviderResultKind.Ambiguous => new RetryDecision(RetryClassification.Ambiguous, RetryAction.Reconcile),
        ProviderResultKind.PermanentError => new RetryDecision(RetryClassification.PermanentProvider, RetryAction.Terminal),
        // Conservative "effect unprovable" mapping: in a reconciliation context a NotFound cannot
        // prove the create did not take effect, so route to ManualReview rather than blindly retry.
        ProviderResultKind.NotFound => new RetryDecision(RetryClassification.Ambiguous, RetryAction.ManualReview),

        ProviderResultKind.Created or ProviderResultKind.AlreadyExists =>
            throw new ArgumentException(
                $"Classifier must not be called for successful outcome '{result.Kind}'.",
                nameof(result)),

        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Kind, "Unknown ProviderResultKind."),
    };

    /// <summary>
    /// Maps an unexpected adapter or transport exception to a <see cref="RetryDecision"/>.
    /// A raw exception means an internal defect — adapters are responsible for normalizing
    /// transient vs ambiguous outcomes into <see cref="ProviderResult"/>; anything that escapes
    /// as an exception is an internal defect routed to the DLQ (spec 06 §15.1).
    /// </summary>
    /// <param name="ex">The unexpected exception.</param>
    /// <returns>A decision of (InternalDefect, Poison).</returns>
    public RetryDecision ClassifyException(Exception ex) =>
        new(RetryClassification.InternalDefect, RetryAction.Poison);
}
