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
    /// <summary>
    /// Moves or amends an existing booking at the provider (spec 06/08).
    ///
    /// <para>Distinct from create-then-cancel on purpose: §9.4.8 requires a modification to
    /// <b>preserve the original reservation until a replacement is secured</b>. Cancelling first
    /// would surrender the seat and leave the guest with nothing if the new time is unavailable —
    /// so this is one provider call that either moves the booking or leaves it exactly as it was.</para>
    ///
    /// <para>Carries the same <c>ProviderIdempotencyKey</c> discipline as create: a redelivery
    /// must not apply the amendment twice. Implementations that cannot amend in place should
    /// return <see cref="ProviderResultKind.PermanentError"/> rather than emulating it with a
    /// cancel-and-recreate, which is a different set of side effects.</para>
    /// </summary>
    /// <param name="providerReference">The existing booking's reference at the provider.</param>
    Task<ProviderResult> ModifyBookingAsync(
        string providerAccount,
        string providerReference,
        ProviderBookingRequest request,
        CancellationToken ct = default);

}
