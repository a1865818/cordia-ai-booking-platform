using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Services.Retention;

/// <summary>
/// Cross-tenant retention driver (spec 14 §24.3). Enumerates the distinct
/// tenants present in the three swept tables (IgnoreQueryFilters — this is a
/// background entry point authoritative on the ids it discovers, same pattern
/// as <see cref="RetentionSweepHandler"/>) and runs the three per-tenant
/// sweeps for each. Per-tenant failures are logged and skipped. After the
/// table sweeps it prunes expired encrypted objects (<see cref="IObjectStore"/>,
/// e.g. 72h raw audio) and audits each removal
/// (<c>privacy.retention.object_pruned</c>).
///
/// Each tenant is swept in its OWN DI scope — a fresh <see cref="RetentionSweepHandler"/>
/// over a fresh <see cref="ApplicationDbContext"/> (M4). A tenant whose SaveChanges throws
/// (e.g. a <c>DbUpdateConcurrencyException</c> on a session's version token touched
/// mid-sweep) has its dirty change tracker disposed with the scope, so its aborted entities
/// can never be re-attempted inside a LATER tenant's SaveChanges and silently halt retention
/// for every tenant after it. The "one tenant's failure never blocks another" guarantee is
/// therefore structural, not just a catch-and-continue.
/// </summary>
public sealed class RetentionSweeper(
    IServiceScopeFactory scopeFactory,
    IObjectStore objectStore,
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
                // Fresh scope ⇒ fresh DbContext + handler per tenant (M4): a failed tenant's
                // aborted change tracker is disposed with the scope and cannot carry dirty
                // entities into the next tenant's SaveChanges.
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<RetentionSweepHandler>();

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

        total += await SweepExpiredObjectsAsync(now, ct);

        return total;
    }

    /// <summary>
    /// Prunes expired encrypted objects, writing one <c>privacy.retention.object_pruned</c> audit
    /// row per removal (append-only, Inv-14; metadata carries control-plane fields only — never
    /// object content). The audit rows are COMMITTED BEFORE the blobs are deleted (L1): the store
    /// is identified non-destructively (<see cref="IObjectStore.ScanExpiredAsync"/>), the trail is
    /// saved, then each blob is removed. If the audit save throws, nothing is deleted and the same
    /// objects are re-identified and re-audited next cycle; if a delete fails after the commit, the
    /// object is re-identified and re-audited next cycle too (at-least-once pruning + audit — a
    /// removal is never left with no trail, matching the platform's audit posture).
    /// </summary>
    private async Task<int> SweepExpiredObjectsAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var expired = await objectStore.ScanExpiredAsync(now, ct);
            if (expired.Count == 0)
                return 0;

            // Own scope for the audit writes — same isolation posture as the per-tenant sweeps.
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            foreach (var obj in expired)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = obj.TenantId,
                    EventId = $"audit-{Guid.NewGuid():N}",
                    EventType = "privacy.retention.object_pruned",
                    Actor = "system",
                    SubjectRef = obj.ObjectId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        reason = obj.Reason,
                        purpose = obj.Purpose,
                        expires_at = obj.ExpiresAt,
                    }),
                    OccurredAt = now,
                });
            }
            // Commit the trail FIRST — only then remove the bytes (L1).
            await db.SaveChangesAsync(ct);

            // Delete each blob in ISOLATION (B5): a single DeleteAsync failure (a locked file,
            // permission, or transient I/O error) must not skip every remaining expired object.
            // The audit rows are already committed, so a blob left behind is simply re-identified
            // and re-deleted next cycle (idempotent) — same per-item fault isolation as the
            // per-tenant loop above.
            var deleted = 0;
            foreach (var obj in expired)
            {
                try
                {
                    await objectStore.DeleteAsync(obj.TenantId, obj.Purpose, obj.ObjectId, ct);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex,
                        "Retention sweep failed to delete pruned object tenant={TenantId} purpose={Purpose} id={ObjectId}; retrying next cycle",
                        obj.TenantId, obj.Purpose, obj.ObjectId);
                }
            }

            logger.LogInformation("Retention sweep pruned {Count} expired objects", deleted);
            return deleted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Object pruning must never block the table sweeps' schedule.
            logger.LogError(ex, "Expired-object sweep failed; continuing");
            return 0;
        }
    }

    private async Task<IReadOnlyList<string>> EnumerateTenantsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
