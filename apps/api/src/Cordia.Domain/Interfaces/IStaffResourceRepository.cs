using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Access to named staff members a tenant can assign to work.</summary>
public interface IStaffResourceRepository
{
    Task<IReadOnlyList<StaffResource>> ListActiveAsync(string tenantId, CancellationToken ct = default);

    /// <summary>All staff members for the tenant, including inactive ones.</summary>
    Task<IReadOnlyList<StaffResource>> ListAsync(string tenantId, CancellationToken ct = default);

    Task<StaffResource?> GetAsync(string tenantId, string resourceId, CancellationToken ct = default);

    Task AddAsync(StaffResource resource, CancellationToken ct = default);
}
