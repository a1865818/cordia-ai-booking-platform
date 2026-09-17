using Cordia.Domain.Capacity;

namespace Cordia.Domain.Interfaces;

/// <summary>Reads the configured capacity-ladder rung for a (tenant, provider) pair (#106).</summary>
public interface ITenantCapacityPrimitiveRepository
{
    /// <summary>The configured rung, or <see langword="null"/> when none is configured.</summary>
    Task<CapacityPrimitive?> GetAsync(string tenantId, string providerName, CancellationToken ct = default);
}
