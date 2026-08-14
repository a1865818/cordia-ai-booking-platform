using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class TenantDepositPolicyRepository(ApplicationDbContext db) : ITenantDepositPolicyRepository
{
    public Task<TenantDepositPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
        => db.TenantDepositPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

    public async Task UpsertAsync(TenantDepositPolicy policy, CancellationToken ct = default)
    {
        var existing = await db.TenantDepositPolicies
            .FirstOrDefaultAsync(p => p.TenantId == policy.TenantId, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            policy.CreatedAt = now;
            policy.UpdatedAt = now;
            db.TenantDepositPolicies.Add(policy);
        }
        else
        {
            existing.Require = policy.Require;
            existing.AmountPolicy = policy.AmountPolicy;
            existing.NoShowOnly = policy.NoShowOnly;
            existing.RefundWindow = policy.RefundWindow;
            existing.AmountMinor = policy.AmountMinor;
            existing.Currency = policy.Currency;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
