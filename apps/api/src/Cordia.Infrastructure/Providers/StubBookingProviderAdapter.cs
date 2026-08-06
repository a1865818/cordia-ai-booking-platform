using System.Collections.Concurrent;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
///   Deterministic in-memory booking provider adapter used for development and as the default
///   implementation. Integration tests substitute their own adapter.
///   Registered as a singleton so the in-memory store persists across DI scopes.
/// </summary>
public sealed class StubBookingProviderAdapter : IBookingProviderAdapter
{
    /// <inheritdoc/>
    public string ProviderName => "stub";

    // (providerAccount, providerIdempotencyKey) → providerReference
    private readonly ConcurrentDictionary<(string, string), string> _byKey = new();

    // (providerAccount, providerReference) → providerReference (presence check)
    private readonly ConcurrentDictionary<(string, string), string> _byReference = new();

    /// <inheritdoc/>
    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
    {
        var key = (request.ProviderAccount, request.ProviderIdempotencyKey);

        if (_byKey.TryGetValue(key, out var existing))
            return Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, existing));

        // Generate a deterministic reference from the idempotency key.
        var reference = $"ext_{request.ProviderIdempotencyKey}";
        _byKey[key] = reference;
        _byReference[(request.ProviderAccount, reference)] = reference;

        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, reference));
    }

    /// <inheritdoc/>
    public Task<ProviderResult> LookupByIdempotencyKeyAsync(
        string providerAccount, string providerIdempotencyKey, CancellationToken ct = default)
    {
        return _byKey.TryGetValue((providerAccount, providerIdempotencyKey), out var reference)
            ? Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, reference))
            : Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
    }

    /// <inheritdoc/>
    public Task<ProviderResult> LookupByReferenceAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
    {
        return _byReference.ContainsKey((providerAccount, providerReference))
            ? Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, providerReference))
            : Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
    }

    /// <inheritdoc/>
    public Task<ProviderResult> GetStatusAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
    {
        return _byReference.ContainsKey((providerAccount, providerReference))
            ? Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, providerReference))
            : Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
    }

    /// <summary>
    /// Amends an existing booking in place. The reference is PRESERVED — the booking moved, it
    /// was not replaced — and the request key makes a redelivery idempotent. An unknown
    /// reference returns NotFound so the caller settles from current state rather than
    /// reconciling a booking this stub never held.
    /// </summary>
    public Task<ProviderResult> ModifyBookingAsync(
        string providerAccount,
        string providerReference,
        ProviderBookingRequest request,
        CancellationToken ct = default)
    {
        // Unknown reference: nothing to amend. NotFound (not an error) so the caller can settle
        // from current state rather than reconciling a booking the provider never had.
        if (!_byReference.ContainsKey((providerAccount, providerReference)))
            return Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

        // Idempotent by the request key: a redelivery of the SAME amendment must not apply it
        // twice, and must report the same outcome it did the first time.
        if (_byKey.TryGetValue((providerAccount, request.ProviderIdempotencyKey), out var existing))
            return Task.FromResult(new ProviderResult(ProviderResultKind.AlreadyExists, existing));

        _byKey[(providerAccount, request.ProviderIdempotencyKey)] = providerReference;

        // The reference is PRESERVED across an amendment — the booking moved, it was not
        // replaced. A new reference here would strand every record pointing at the old one.
        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, providerReference));
    }

    /// <summary>
    ///   Cancel a booking. If the reference is known, removes it from both stores and returns
    ///   <see cref="ProviderResultKind.Created"/> (treated as "cancel acknowledged"). If unknown, returns
    ///   <see cref="ProviderResultKind.NotFound"/>.
    /// </summary>
    public Task<ProviderResult> CancelBookingAsync(
        string providerAccount, string providerReference, CancellationToken ct = default)
    {
        if (!_byReference.TryRemove((providerAccount, providerReference), out _))
            return Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

        // Also remove the key-based entry for this reference.
        var keysToRemove = _byKey
            .Where(kvp => kvp.Key.Item1 == providerAccount && kvp.Value == providerReference)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var k in keysToRemove)
            _byKey.TryRemove(k, out _);

        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, providerReference));
    }
}
