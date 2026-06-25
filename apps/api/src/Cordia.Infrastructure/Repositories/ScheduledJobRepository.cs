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
        var results = await db.ScheduledJobs
            .AsNoTracking()
            .Where(j => j.Status == ScheduledJobStatus.Pending && j.RunAfter <= now)
            .OrderBy(j => j.RunAfter)
            .Take(limit)
            .ToListAsync(ct);

        return results;
    }
}
