using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IScheduledJobRepository"/>.</summary>
public class ScheduledJobRepository(ApplicationDbContext db) : IScheduledJobRepository
{
    public async Task AddAsync(ScheduledJob job, CancellationToken ct = default)
    {
        // Inv-12 choke point: scheduled-job payloads are queue envelopes too (see OutboxRepository).
        job.Payload = SensitiveDataMask.SanitizeJson(job.Payload);
        db.ScheduledJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> ExistsAsync(string tenantId, string jobId, CancellationToken ct = default)
        => db.Set<ScheduledJob>()
            .IgnoreQueryFilters()
            .AnyAsync(j => j.TenantId == tenantId && j.JobId == jobId, ct);

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

    public async Task<IReadOnlyList<ScheduledJob>> ClaimDueAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Cross-tenant worker claim (T5, §5.1 mirror). Open an explicit short transaction:
        // SELECT FOR UPDATE SKIP LOCKED acquires row locks; committing releases them so the
        // lease fields become visible to other workers before next tick.
        //
        // The predicate status::text <> 'done' AND status::text <> 'dead' claims both
        // pending jobs AND reclaims claimed jobs whose lease expired — intentional
        // correctness-over-index choice. The partial index (WHERE status='pending') is used
        // for the Pending→Claimed hot path; the expired-lease reclaim scans a small Claimed
        // set (rare) outside the index. The cast to ::text is required because Postgres will
        // not implicitly compare an enum column to a text literal in raw SQL without it.
        //
        // CRITICAL (§15.2): This method NEVER changes Attempt. Attempt is incremented ONLY
        // by RescheduleAsync. This ensures that a worker restart / lease-expiry reclaim
        // redelivers the SAME message_id (sched-{jobId}-att{attempt}) so the inbox dedups
        // the duplicate — a genuine retry via RescheduleAsync yields attempt+1 → new message_id.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Set<ScheduledJob>()
            .FromSqlRaw(
                "SELECT * FROM public.scheduled_jobs " +
                "WHERE status::text <> 'done' AND status::text <> 'dead' " +
                "AND run_after <= {0} " +
                "AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY run_after " +
                "LIMIT {1} " +
                "FOR UPDATE SKIP LOCKED",
                now,
                limit)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Status = ScheduledJobStatus.Claimed;
            row.LeaseOwner = leaseOwner;
            row.LeaseExpiresAt = leaseExpiresAt;
            // Attempt is NOT touched — §15.2 invariant.
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rows;
    }

    public Task MarkDoneAsync(string tenantId, string jobId, CancellationToken ct = default)
        => SetTerminalStatusAsync(tenantId, jobId, ScheduledJobStatus.Done, ct);

    public Task MarkDeadAsync(string tenantId, string jobId, CancellationToken ct = default)
        => SetTerminalStatusAsync(tenantId, jobId, ScheduledJobStatus.Dead, ct);

    /// <summary>
    /// Sets a terminal <paramref name="status"/> (Done/Dead) and clears the lease via
    /// ExecuteUpdateAsync (single UPDATE, no entity load). IgnoreQueryFilters: the poller's
    /// DbContext may not be scoped to <paramref name="tenantId"/>, so the WHERE predicate supplies
    /// explicit tenant scope.
    /// </summary>
    private Task SetTerminalStatusAsync(
        string tenantId,
        string jobId,
        ScheduledJobStatus status,
        CancellationToken ct)
        => db.Set<ScheduledJob>()
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.JobId == jobId)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(j => j.Status, status)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);

    public Task RescheduleAsync(
        string tenantId,
        string jobId,
        DateTimeOffset newRunAfter,
        CancellationToken ct = default)
    {
        // This is the ONLY place Attempt is incremented (§15.2). RescheduleAsync represents a
        // genuine retry decision — each increment yields a new message_id in the poller
        // (sched-{jobId}-att{newAttempt}), so the inbox treats it as a fresh delivery rather
        // than deduplicating it as a duplicate of the previous attempt.
        return db.Set<ScheduledJob>()
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.JobId == jobId)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(j => j.Status, ScheduledJobStatus.Pending)
                    .SetProperty(j => j.RunAfter, newRunAfter)
                    .SetProperty(j => j.Attempt, j => j.Attempt + 1)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null),
                ct);
    }
}
