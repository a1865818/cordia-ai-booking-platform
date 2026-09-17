using Cordia.Domain.Capacity;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class TenantCapacityPrimitiveRepository(ApplicationDbContext db) : ITenantCapacityPrimitiveRepository
{
    public async Task<CapacityPrimitive?> GetAsync(string tenantId, string providerName, CancellationToken ct = default)
        => await db.TenantCapacityPrimitives
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ProviderName == providerName)
            .Select(p => (CapacityPrimitive?)p.Primitive)
            .FirstOrDefaultAsync(ct);
}
