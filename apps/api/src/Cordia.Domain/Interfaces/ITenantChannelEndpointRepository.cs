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
}
