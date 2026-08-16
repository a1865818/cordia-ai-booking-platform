using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class TenantProfileRepository(ApplicationDbContext db) : ITenantProfileRepository
{
    public Task<TenantProfile?> GetAsync(string tenantId, CancellationToken ct = default)
        => db.TenantProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

    public async Task UpsertAsync(TenantProfile profile, CancellationToken ct = default)
    {
        var existing = await db.TenantProfiles
            .FirstOrDefaultAsync(p => p.TenantId == profile.TenantId, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            profile.CreatedAt = now;
            profile.UpdatedAt = now;
            db.TenantProfiles.Add(profile);
        }
        else
        {
            existing.BusinessType = profile.BusinessType;
            existing.WaitlistAutoBook = profile.WaitlistAutoBook;
            existing.HoursJson = profile.HoursJson;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
