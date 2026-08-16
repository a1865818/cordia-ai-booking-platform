using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class OperatorRepository(ApplicationDbContext db) : IOperatorRepository
{
    public Task<Operator?> GetByEmailAsync(string tenantId, string email, CancellationToken ct = default)
        => db.Operators
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Email == email, ct);

    public Task<Operator?> GetByIdAsync(string tenantId, string operatorId, CancellationToken ct = default)
        => db.Operators
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperatorId == operatorId, ct);

    public async Task<Operator?> FindByEmailAcrossTenantsAsync(string email, CancellationToken ct = default)
    {
        // Login has no tenant yet. Emails are unique per tenant; two active hits across
        // tenants must not enumerate — the handler treats null as generic 401.
        var matches = await db.Operators
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Email == email && o.Status == "active")
            .Take(2)
            .ToListAsync(ct);

        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task AddAsync(Operator entity, CancellationToken ct = default)
    {
        db.Operators.Add(entity);
        await db.SaveChangesAsync(ct);
    }
}
