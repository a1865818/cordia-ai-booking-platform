using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Read/write access to the tenant's guest-facing venue name (docs §12 gap 11).</summary>
public interface ITenantDisplayNameRepository
{
    /// <summary>The tenant's display name, or <c>null</c> when it has not set one.</summary>
    Task<string?> GetAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates or replaces the tenant's display name. Idempotent: the tenant is the key, so a
    /// repeat is an update rather than a second row.
    /// </summary>
    Task UpsertAsync(TenantDisplayName displayName, CancellationToken ct = default);
}
