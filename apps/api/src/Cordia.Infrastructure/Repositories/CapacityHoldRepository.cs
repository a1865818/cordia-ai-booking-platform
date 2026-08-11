using Cordia.Domain.Entities;
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
        // ORDERED BY hold id, and that is load-bearing rather than tidiness. Callers iterate this
        // list taking a row lock on each hold; an unordered result lets two transactions releasing
        // overlapping hold sets acquire the same rows in opposite orders and deadlock against each
        // other. Ascending hold id is a total order, so no pair can invert (CLO clause 2).
        var results = await db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.OperationId == operationId && h.Status == HoldStatus.Active)
            .OrderBy(h => h.HoldId)
            .ToListAsync(ct);

        return results;
    }

    public Task<CapacityHold?> GetLatestByOperationAndStepAsync(
        string tenantId,
        string operationId,
        string workflowStep,
        CancellationToken ct = default)
        => db.CapacityHolds
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId
                        && h.OperationId == operationId
                        && h.WorkflowStep == workflowStep)
            .OrderByDescending(h => h.CreatedAt)
            .ThenByDescending(h => h.HoldId)
            .FirstOrDefaultAsync(ct);

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

    /// <inheritdoc/>
    public Task LockAsync(string tenantId, string holdId, CancellationToken ct = default)
        // Raw SQL because EF has no `FOR UPDATE`. Selects a constant rather than the row: the
        // caller wants the LOCK, not the data — it has already read what it needs — and selecting
        // no columns keeps this from being mistaken for a read whose result matters.
        //
        // The lock is released when the ambient transaction commits or rolls back, which is the
        // whole point: it orders this transaction's capacity writes against a concurrent cancel's.
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.capacity_holds WHERE tenant_id = {tenantId} AND hold_id = {holdId} FOR UPDATE",
            ct);
}
