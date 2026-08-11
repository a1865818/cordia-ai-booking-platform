using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ITenantResourceRepository"/>.</summary>
public class TenantResourceRepository(ApplicationDbContext db) : ITenantResourceRepository
{
    public async Task<IReadOnlyList<TenantResource>> ListAsync(
        string tenantId, string? kind, CancellationToken ct = default)
        => await db.TenantResources
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.Active)
            .Where(r => kind == null || r.Kind == kind)
            .OrderBy(r => r.Kind).ThenBy(r => r.ResourceId)
            .ToListAsync(ct);
}
