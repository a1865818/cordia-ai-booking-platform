using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ICapacityHoldRepository"/>.</summary>
public class CapacityHoldRepository(ApplicationDbContext db) : ICapacityHoldRepository
{
    public Task<CapacityHold?> GetAsync(string tenantId, string holdId, CancellationToken ct = default)
        => db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.TenantId == tenantId && h.HoldId == holdId, ct);

    public async Task AddAsync(CapacityHold hold, CancellationToken ct = default)
    {
        db.CapacityHolds.Add(hold);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CapacityHold>> GetActiveByOperationAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
    {
        var results = await db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.OperationId == operationId && h.Status == HoldStatus.Active)
            .ToListAsync(ct);

        return results;
    }

    public Task<CapacityHold?> GetByOperationAndStepAsync(
        string tenantId,
        string operationId,
        string workflowStep,
        CancellationToken ct = default)
        => db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                h => h.TenantId == tenantId
                     && h.OperationId == operationId
                     && h.WorkflowStep == workflowStep,
                ct);

    public async Task<IReadOnlyList<CapacityHold>> ListExpiredActiveAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        // Cross-tenant sweep: IgnoreQueryFilters so the ambient tenant filter is bypassed.
        var results = await db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.Status == HoldStatus.Active && h.ExpiresAt <= now)
            .OrderBy(h => h.ExpiresAt)
            .Take(limit)
            .ToListAsync(ct);

        return results;
    }
}
