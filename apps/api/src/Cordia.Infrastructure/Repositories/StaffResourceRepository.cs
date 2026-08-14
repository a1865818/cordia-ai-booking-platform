using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class StaffResourceRepository(ApplicationDbContext db) : IStaffResourceRepository
{
    public async Task<IReadOnlyList<StaffResource>> ListActiveAsync(string tenantId, CancellationToken ct = default)
        => await db.StaffResources
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Active)
            .OrderBy(r => r.Name)
            .ThenBy(r => r.ResourceId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StaffResource>> ListAsync(string tenantId, CancellationToken ct = default)
        => await db.StaffResources
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ThenBy(r => r.ResourceId)
            .ToListAsync(ct);

    public Task<StaffResource?> GetAsync(string tenantId, string resourceId, CancellationToken ct = default)
        => db.StaffResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ResourceId == resourceId, ct);

    public async Task AddAsync(StaffResource resource, CancellationToken ct = default)
    {
        db.StaffResources.Add(resource);
        await db.SaveChangesAsync(ct);
    }
}
