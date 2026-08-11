using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class TenantDisplayNameRepository(ApplicationDbContext db) : ITenantDisplayNameRepository
{
    public async Task<string?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var name = await db.TenantDisplayNames
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => t.DisplayName)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public async Task UpsertAsync(TenantDisplayName displayName, CancellationToken ct = default)
    {
        // Tracked (not AsNoTracking): this is the one place the row is written, and the update
        // path needs change tracking.
        var existing = await db.TenantDisplayNames
            .FirstOrDefaultAsync(t => t.TenantId == displayName.TenantId, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            displayName.CreatedAt = now;
            displayName.UpdatedAt = now;
            db.TenantDisplayNames.Add(displayName);
        }
        else
        {
            existing.DisplayName = displayName.DisplayName;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
