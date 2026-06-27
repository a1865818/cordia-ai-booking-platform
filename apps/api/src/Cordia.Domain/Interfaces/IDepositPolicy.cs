using Cordia.Domain.Fulfillment;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Per-restaurant deposit toggle (spec 09 owns the real policy).
/// Returns whether a deposit is required for this operation and, if so, the amount/currency.
/// </summary>
public interface IDepositPolicy
{
    Task<DepositRequirement> GetRequirementAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
