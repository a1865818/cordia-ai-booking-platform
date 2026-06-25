using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IScheduledJobRepository"/>.</summary>
public class ScheduledJobRepository(ApplicationDbContext db) : IScheduledJobRepository
{
    public async Task AddAsync(ScheduledJob job, CancellationToken ct = default)
    {
        db.ScheduledJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ScheduledJob>> ListDueAsync(
        string tenantId,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        // Background-worker claim: authoritative on the tenantId argument. A worker is not
        // request-scoped to one tenant, so bypass the ambient context filter (IgnoreQueryFilters)
        // and re-assert TenantId == tenantId — otherwise a context scoped to another tenant (or
        // unset) would silently return the wrong tenant's jobs / nothing (P2).
        var results = await db.ScheduledJobs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.Status == ScheduledJobStatus.Pending && j.RunAfter <= now)
            .OrderBy(j => j.RunAfter)
            .Take(limit)
            .ToListAsync(ct);

        return results;
    }
}
