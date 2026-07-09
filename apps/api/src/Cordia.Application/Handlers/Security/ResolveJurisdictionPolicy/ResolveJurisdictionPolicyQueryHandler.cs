using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;

namespace Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;

/// <summary>Maps the wire channel onto <see cref="TenantChannel"/> and delegates to the provider.</summary>
public sealed class ResolveJurisdictionPolicyQueryHandler(IJurisdictionPolicyProvider provider)
    : IRequestHandler<ResolveJurisdictionPolicyQuery, JurisdictionPolicy>
{
    internal static readonly IReadOnlyDictionary<string, TenantChannel> ChannelMap =
        new Dictionary<string, TenantChannel>(StringComparer.OrdinalIgnoreCase)
        {
            ["voice"] = TenantChannel.Voice,
            ["sms"] = TenantChannel.Messaging,
            ["messaging"] = TenantChannel.Messaging,
            ["web"] = TenantChannel.Web,
            ["email"] = TenantChannel.Email,
        };

    public Task<JurisdictionPolicy> Handle(ResolveJurisdictionPolicyQuery query, CancellationToken ct)
    {
        var channel = ChannelMap[query.Channel];
        return Task.FromResult(provider.Resolve(query.TenantId, channel));
    }
}
