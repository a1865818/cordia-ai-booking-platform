namespace Cordia.Domain.Fulfillment;

/// <summary>Normalized outcome kinds returned by a booking provider adapter.</summary>
public enum ProviderResultKind
{
    /// <summary>The booking was created successfully.</summary>
    Created,

    /// <summary>The booking already exists under the supplied idempotency key (provider-side idempotency, Inv-7).</summary>
    AlreadyExists,

    /// <summary>No booking found for the supplied key or reference.</summary>
    NotFound,

    /// <summary>No capacity available for the requested slot.</summary>
    NoCapacity,

    /// <summary>The request was rejected by the provider (validation or policy failure).</summary>
    Rejected,

    /// <summary>Payment action required before the provider can confirm (e.g. 3-DS challenge).</summary>
    PaymentActionRequired,

    /// <summary>Authentication or authorisation failure against the provider.</summary>
    AuthError,

    /// <summary>Transient provider failure; safe to retry under the same idempotency key.</summary>
    Transient,

    /// <summary>Ambiguous outcome — the effect of the call is unknown (e.g. timeout-after-send). Reconcile before any repeat.</summary>
    Ambiguous,

    /// <summary>Permanent provider error; retrying will not help.</summary>
    PermanentError,
}
