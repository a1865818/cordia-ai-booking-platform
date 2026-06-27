using Cordia.Domain.Fulfillment;

namespace Cordia.Domain.Interfaces;

/// <summary>Adapter contract for an external booking provider (spec 06 §4, §16.2).</summary>
public interface IBookingProviderAdapter
{
    /// <summary>Identifies the provider this adapter connects to (e.g. "stub", "acuity").</summary>
    string ProviderName { get; }

    /// <summary>Create a new booking at the provider using a stable idempotency key (Inv-2/3).</summary>
    Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default);

    /// <summary>
    ///   Lookup a booking by provider idempotency key — step 1 of the §16.2 reconciliation order
    ///   (key → reference → status endpoint). Use this first after an ambiguous outcome (Inv-7).
    /// </summary>
    Task<ProviderResult> LookupByIdempotencyKeyAsync(string providerAccount, string providerIdempotencyKey, CancellationToken ct = default);

    /// <summary>
    ///   Lookup a booking by provider reference — step 2 of the §16.2 reconciliation order
    ///   (key → reference → status endpoint). Use when the idempotency-key lookup is inconclusive.
    /// </summary>
    Task<ProviderResult> LookupByReferenceAsync(string providerAccount, string providerReference, CancellationToken ct = default);

    /// <summary>Poll the provider status endpoint for a known booking reference — step 3 of the §16.2 reconciliation order.</summary>
    Task<ProviderResult> GetStatusAsync(string providerAccount, string providerReference, CancellationToken ct = default);

    /// <summary>Cancel an existing booking at the provider.</summary>
    Task<ProviderResult> CancelBookingAsync(string providerAccount, string providerReference, CancellationToken ct = default);
}
