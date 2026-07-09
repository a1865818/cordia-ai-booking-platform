using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Services.Retention;

/// <summary>
/// Cross-tenant retention driver (spec 14 §24.3). Enumerates the distinct
/// tenants present in the three swept tables (IgnoreQueryFilters — this is a
/// background entry point authoritative on the ids it discovers, same pattern
/// as <see cref="RetentionSweepHandler"/>) and runs the three per-tenant
/// sweeps for each. Per-tenant failures are logged and skipped.
/// </summary>
public sealed class RetentionSweeper(
    ApplicationDbContext db,
    RetentionSweepHandler handler,
    ILogger<RetentionSweeper> logger) : IRetentionSweeper
{
    public async Task<int> SweepAllTenantsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var tenants = await EnumerateTenantsAsync(ct);
        var total = 0;

        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var idempotency = await handler.SweepIdempotencyRecordsAsync(tenantId, now, ct: ct);
                var sessions = await handler.SweepConversationSessionsAsync(tenantId, now, ct: ct);
                var grants = await handler.SweepVerificationGrantsAsync(tenantId, now, ct: ct);

                var pruned = idempotency.Pruned + sessions.Pruned + grants.Pruned;
                total += pruned;
                if (pruned > 0)
                {
                    logger.LogInformation(
                        "Retention sweep tenant={TenantId}: idempotency={Idempotency} sessions={Sessions} grants={Grants}",
                        tenantId, idempotency.Pruned, sessions.Pruned, grants.Pruned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's failure must never block another's retention.
                logger.LogError(ex, "Retention sweep failed for tenant {TenantId}; continuing", tenantId);
            }
        }

        return total;
    }

    private async Task<IReadOnlyList<string>> EnumerateTenantsAsync(CancellationToken ct)
    {
        var idempotencyTenants = await db.IdempotencyRecords
            .IgnoreQueryFilters().Select(r => r.TenantId).Distinct().ToListAsync(ct);
        var sessionTenants = await db.ConversationSessions
            .IgnoreQueryFilters().Select(s => s.TenantId).Distinct().ToListAsync(ct);
        var grantTenants = await db.VerificationGrants
            .IgnoreQueryFilters().Select(g => g.TenantId).Distinct().ToListAsync(ct);

        return idempotencyTenants
            .Union(sessionTenants)
            .Union(grantTenants)
            .OrderBy(t => t)
            .ToList();
    }
}
