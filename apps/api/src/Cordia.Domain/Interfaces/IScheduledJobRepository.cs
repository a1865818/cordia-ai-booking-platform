using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for scheduled jobs.</summary>
public interface IScheduledJobRepository
{
    Task AddAsync(ScheduledJob job, CancellationToken ct = default);

    /// <summary>Returns up to <paramref name="limit"/> pending jobs with RunAfter ≤ <paramref name="now"/>,
    /// ordered by RunAfter ascending. Uses AsNoTracking.</summary>
    Task<IReadOnlyList<ScheduledJob>> ListDueAsync(
        string tenantId,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default);
}
