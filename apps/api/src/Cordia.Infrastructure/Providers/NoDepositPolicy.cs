using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Default no-op deposit policy: deposit is never required.
/// Spec 09 replaces this with the real per-restaurant policy.
/// </summary>
public sealed class NoDepositPolicy : IDepositPolicy
{
    public Task<DepositRequirement> GetRequirementAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
        => Task.FromResult(DepositRequirement.None);
}
