using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IDepositSessionRepository"/>.</summary>
public class DepositSessionRepository(ApplicationDbContext db) : IDepositSessionRepository
{
    public async Task AddAsync(DepositSession session, CancellationToken ct = default)
    {
        db.DepositSessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public Task<DepositSession?> GetAsync(string tenantId, string depositSessionId, CancellationToken ct = default)
        => db.DepositSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.DepositSessionId == depositSessionId, ct);

    public Task<DepositSession?> GetByOperationAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
        => db.DepositSessions
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.OperationId == operationId)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string depositSessionId,
        long expectedVersion,
        DepositStatus expectedStatus,
        DepositStatus next,
        DateTimeOffset? paidAt = null,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<DepositSession>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.DepositSessionId == depositSessionId && e.Status == expectedStatus,
            s =>
            {
                s.SetProperty(x => x.Status, next);
                if (paidAt.HasValue)
                    s.SetProperty(x => x.PaidAt, paidAt.Value);
            },
            ct);
}
