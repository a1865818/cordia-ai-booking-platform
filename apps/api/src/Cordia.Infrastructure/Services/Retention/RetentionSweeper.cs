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
    Cordia.Infrastructure.Observability.Metrics metrics,
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

                // Surveys, does not erase: contact data inside an immutable hashed intent
                // revision cannot be redacted without breaking the idempotency chain derived
                // from payload_hash (Inv-2/3). Counting it is what turns "how much un-erasable
                // guest data do we hold?" from an unanswerable question into a number — which is
                // the whole point, and only true if something actually calls this.
                var unerasable = await handler.SurveyUnerasableContactAsync(tenantId, now, ct: ct);

                // The retention ruling's payoff (owner decision 2026-08-06). Contact written
                // out-of-band is a plain row, so this is a real DELETE — the thing the inline
                // shape made structurally impossible. The canonical payload keeps its
                // `contact_ref`, so every hash still verifies; the ref simply stops
                // dereferencing, which is exactly what erasure should look like from outside.
                var contactCutoff = now - RetentionPolicy.DefaultSessionAnonymizationWindow;
                var erasedContacts = await scope.ServiceProvider
                    .GetRequiredService<IGuestContactRepository>()
                    .EraseOlderThanAsync(tenantId, contactCutoff, ct);

                if (erasedContacts > 0)
                {
                    total += erasedContacts;
                    logger.LogInformation(
                        "Retention sweep tenant={TenantId}: erased {Count} guest contact row(s).",
                        tenantId, erasedContacts);

                    // The trail for the one sweep that had none. Every other sweep appends a
                    // `privacy.retention.*` row and the mint path audits `guest_contact.minted`, so
                    // the only table the platform can truly erase from was the only one whose
                    // erasures existed nowhere but a log line — leaving a mint with no matching
                    // deletion and no way to answer "was this guest's contact removed, and when?".
                    //
                    // AGGREGATE, not per-row: `ExecuteDeleteAsync` is a set-based delete that
                    // cannot return the refs it removed, and re-reading them first would mean
                    // holding the very identifiers this is erasing. Count plus cutoff is what can
                    // be recorded truthfully, and it is enough to reconcile against the mints.
                    var auditDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    auditDb.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"audit-{Guid.NewGuid():N}",
                        EventType = "privacy.retention.contact_erased",
                        Actor = "system",
                        SubjectRef = "guest_contacts",
                        Metadata = JsonSerializer.Serialize(new
                        {
                            erased = erasedContacts,
                            cutoff = contactCutoff,
                        }),
                        OccurredAt = now,
                    });
                    await auditDb.SaveChangesAsync(ct);
                }

                var pruned = idempotency.Pruned + sessions.Pruned + grants.Pruned;
                total += pruned;
                if (pruned > 0)
                {
                    logger.LogInformation(
                        "Retention sweep tenant={TenantId}: idempotency={Idempotency} sessions={Sessions} grants={Grants}",
                        tenantId, idempotency.Pruned, sessions.Pruned, grants.Pruned);
                }

                // Logged separately and at warning level: this is data the sweep CANNOT erase, so
                // it is a standing exposure rather than a completed action. Zero is the normal
                // case and stays silent.
                if (unerasable.Skipped > 0)
                {
                    // Emitted as a counter as well as a log line. The log answers "is there any?"
                    // once per sweep; the counter answers "is it growing?", which is the question
                    // the deferred spec 12/14 retention ruling actually turns on — the choice
                    // between the three options in §14 depends on whether this is a bounded
                    // legacy set or an accumulating one, and a Warning buried in the log cannot
                    // tell anyone that.
                    metrics.RetentionUnerasableContactTotal.Add(unerasable.Skipped);

                    logger.LogWarning(
                        "Retention sweep tenant={TenantId}: {Count} intent revision(s) past the window still "
                        + "hold guest contact data. These cannot be erased — the payload is hashed and the "
                        + "idempotency chain descends from that hash (see docs frontend-api-wiring.md §14).",
                        tenantId, unerasable.Skipped);
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

        // The table the guest-contact erasure actually reads. Without it, a console-only tenant
        // whose idempotency records, sessions and grants have all already been pruned on their
        // shorter deadlines is never enumerated again — so its guest contacts are retained past
        // the window indefinitely, which is the permanent-PII outcome the erasure exists to remove.
        //
        // `booking_operations` is deliberately NOT unioned in, though it would also widen the set.
        // It is the largest table in the schema and is never pruned, and an unqualified
        // SELECT DISTINCT over it has no index-only path — so every sweep tick would pay a full,
        // monotonically growing scan to discover a handful of tenant ids. What that would add is
        // the un-erasable-contact SURVEY (a metric) for a tenant with nothing else left; a missing
        // metric reading is worth far less than a table scan on every cycle.
        var guestContactTenants = await db.GuestContacts
            .IgnoreQueryFilters().Select(g => g.TenantId).Distinct().ToListAsync(ct);

        return idempotencyTenants
            .Union(sessionTenants)
            .Union(grantTenants)
            .Union(guestContactTenants)
            .OrderBy(t => t)
            .ToList();
    }
}
