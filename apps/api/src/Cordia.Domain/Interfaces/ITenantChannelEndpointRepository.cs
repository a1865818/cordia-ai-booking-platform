using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="TenantChannelEndpoint"/> aggregate (cross-tenant ingress resolver).</summary>
public interface ITenantChannelEndpointRepository
{
    Task AddAsync(TenantChannelEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// Resolves the active endpoint for the given channel/signal combination without any tenant filter.
    /// Returns null when no active endpoint matches (Inv-13).
    /// </summary>
    Task<TenantChannelEndpoint?> ResolveActiveAsync(
        TenantChannel channel,
        SignalType signalType,
        string signalValue,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the active endpoint(s) for the given channel/signal combination without any tenant
    /// filter, returning AT MOST TWO rows so the caller can distinguish the three ingress outcomes
    /// (spec-15 §7, Inv-13): zero rows ⇒ <c>tenant_not_resolved</c>, exactly one ⇒ resolved, more
    /// than one ⇒ <c>ambiguous_endpoint</c>. The <c>uq_channel_signal_active</c> partial-unique
    /// index normally guarantees at most one, but the resolver still fails closed on a duplicate
    /// rather than silently taking the first.
    /// </summary>
    Task<IReadOnlyList<TenantChannelEndpoint>> ResolveAllActiveAsync(
        TenantChannel channel,
        SignalType signalType,
        string signalValue,
        CancellationToken ct = default);
}
