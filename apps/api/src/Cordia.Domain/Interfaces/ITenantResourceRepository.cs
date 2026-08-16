using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Read access to a tenant's declared services and locations.</summary>
public interface ITenantResourceRepository
{
    /// <summary>
    /// Active resources for the tenant, optionally narrowed to one kind.
    /// AsNoTracking; explicitly tenant-scoped.
    /// </summary>
    Task<IReadOnlyList<TenantResource>> ListAsync(
        string tenantId, string? kind, CancellationToken ct = default);
}
