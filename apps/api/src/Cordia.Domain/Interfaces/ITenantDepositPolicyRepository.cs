using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Read/write access to the tenant deposit policy row.</summary>
public interface ITenantDepositPolicyRepository
{
    Task<TenantDepositPolicy?> GetAsync(string tenantId, CancellationToken ct = default);

    Task UpsertAsync(TenantDepositPolicy policy, CancellationToken ct = default);
}
