using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Read/write access to the tenant business profile.</summary>
public interface ITenantProfileRepository
{
    Task<TenantProfile?> GetAsync(string tenantId, CancellationToken ct = default);

    Task UpsertAsync(TenantProfile profile, CancellationToken ct = default);
}
