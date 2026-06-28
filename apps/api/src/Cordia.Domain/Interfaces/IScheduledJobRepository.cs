using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for scheduled jobs.</summary>
public interface IScheduledJobRepository
{
    Task AddAsync(ScheduledJob job, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a scheduled job with the given <paramref name="jobId"/> already exists
    /// for <paramref name="tenantId"/> (any status). Used for deterministic deduplication of
    /// expiry job enqueue (FIX 6: one expiry job per hold; re-drive is a no-op).
    /// </summary>
    Task<bool> ExistsAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>Returns up to <paramref name="limit"/> pending jobs with RunAfter ≤ <paramref name="now"/>,
    /// ordered by RunAfter ascending. Uses AsNoTracking.</summary>
    Task<IReadOnlyList<ScheduledJob>> ListDueAsync(
        string tenantId,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant worker claim (T5, §5.1 mirror): claims pending due jobs AND reclaims
    /// claimed jobs whose lease has expired.  Opens its own explicit transaction (SELECT FOR
    /// UPDATE SKIP LOCKED). Sets Status=Claimed + lease fields; NEVER changes Attempt (§15.2).
    /// </summary>
    Task<IReadOnlyList<ScheduledJob>> ClaimDueAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Marks a scheduled job done and clears its lease (ExecuteUpdate, no entity load).</summary>
    Task MarkDoneAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>Marks a scheduled job dead and clears its lease (ExecuteUpdate, no entity load).</summary>
    Task MarkDeadAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Reschedules a job: sets Status=Pending, RunAfter=<paramref name="newRunAfter"/>,
    /// increments Attempt by 1, and clears lease.  This is the ONLY method that bumps Attempt (§15.2).
    /// </summary>
    Task RescheduleAsync(string tenantId, string jobId, DateTimeOffset newRunAfter, CancellationToken ct = default);
}
