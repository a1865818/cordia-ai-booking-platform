using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Messaging;


internal static class ScheduledJobTenantScope
{
    /// <summary>
    /// Re-registers <see cref="IScheduledJobRepository"/> so the poller's claim phase only ever
    /// sees <paramref name="tenantId"/>'s rows. Call AFTER <c>AddInfrastructure</c> — the last
    /// registration is the one resolved.
    /// </summary>
    public static IServiceCollection ScopeScheduledJobClaimToTenant(
        this IServiceCollection services, string tenantId)
    {
        services.AddScoped<IScheduledJobRepository>(sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            return new TenantScopedScheduledJobRepository(db, new ScheduledJobRepository(db), tenantId);
        });

        return services;
    }
}

/// <summary>
/// Delegating <see cref="IScheduledJobRepository"/> whose <see cref="ClaimDueAsync"/> adds a
/// <c>tenant_id</c> predicate to the production claim. Every other member is the real repository.
/// </summary>
internal sealed class TenantScopedScheduledJobRepository(
    ApplicationDbContext db,
    IScheduledJobRepository inner,
    string tenantId) : IScheduledJobRepository
{
    public Task AddAsync(ScheduledJob job, CancellationToken ct = default)
        => inner.AddAsync(job, ct);

    public Task<bool> ExistsAsync(string requestedTenantId, string jobId, CancellationToken ct = default)
        => inner.ExistsAsync(requestedTenantId, jobId, ct);

    public Task<IReadOnlyList<ScheduledJob>> ListDueAsync(
        string requestedTenantId, DateTimeOffset now, int limit, CancellationToken ct = default)
        => inner.ListDueAsync(requestedTenantId, now, limit, ct);

    public Task MarkDoneAsync(string jobTenantId, string jobId, CancellationToken ct = default)
        => inner.MarkDoneAsync(jobTenantId, jobId, ct);

    public Task MarkDeadAsync(string jobTenantId, string jobId, CancellationToken ct = default)
        => inner.MarkDeadAsync(jobTenantId, jobId, ct);

    public Task RescheduleAsync(
        string jobTenantId, string jobId, DateTimeOffset newRunAfter, CancellationToken ct = default)
        => inner.RescheduleAsync(jobTenantId, jobId, newRunAfter, ct);


    public async Task<IReadOnlyList<ScheduledJob>> ClaimDueAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Set<ScheduledJob>()
            .FromSqlRaw(
                "SELECT * FROM public.scheduled_jobs " +
                "WHERE tenant_id = {0} " +
                "AND status::text <> 'done' AND status::text <> 'dead' " +
                "AND run_after <= {1} " +
                "AND (lease_owner IS NULL OR lease_expires_at < {1}) " +
                "ORDER BY run_after " +
                "LIMIT {2} " +
                "FOR UPDATE SKIP LOCKED",
                tenantId,
                now,
                limit)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Status = ScheduledJobStatus.Claimed;
            row.LeaseOwner = leaseOwner;
            row.LeaseExpiresAt = leaseExpiresAt;
            // Attempt is NOT touched — §15.2 invariant, mirrored from production.
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rows;
    }
}
