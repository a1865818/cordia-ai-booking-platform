using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Access to console operator accounts.</summary>
public interface IOperatorRepository
{
    Task<Operator?> GetByEmailAsync(string tenantId, string email, CancellationToken ct = default);

    Task<Operator?> GetByIdAsync(string tenantId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant lookup for web-origin login (tenant is the OUTPUT). Ignores the EF
    /// tenant query filter and returns the single <c>active</c> operator with this email.
    /// Returns null when none match, or when more than one tenant shares the email
    /// (401 generic — do not enumerate tenants).
    /// </summary>
    Task<Operator?> FindByEmailAcrossTenantsAsync(string email, CancellationToken ct = default);

    Task AddAsync(Operator entity, CancellationToken ct = default);
}
