using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Retention;

/// <summary>
/// Retention / privacy-deletion sweeper for <c>idempotency_records</c>
/// (spec 12 §4 "Retention sweepers", §6.3, Inv-14).
///
/// The sweeper is the ONLY sanctioned path for physically deleting
/// <c>idempotency_records</c> rows (spec 12 §3 / §13.3).
///
/// Algorithm (spec 12 §6.3):
///   1. Select candidates: rows whose <c>retention_deadline &lt; now</c>
///      (tenant-scoped via the context's global query filter).
///   2. For each candidate look up the linked <c>booking_operations</c> status.
///   3. <see cref="RetentionPolicy.ShouldPrune"/> — if false: skip (Inv-14 open SLA/dispute
///      window). If true: remove the idempotency record and append an audit event.
///   4. Persist prune + audit atomically in a single <see cref="DbContext.SaveChangesAsync"/>.
///
/// Open work: the concrete retention-deadline formula (booking-life + dispute/audit window)
/// and the jurisdiction-specific retention for transcripts / verification_grants are open
/// decisions (spec 12 §13).  This sweeper is extensible to those tables once those
/// decisions land.
/// </summary>
public class RetentionSweepHandler(ApplicationDbContext db)
{
    /// <summary>
    /// Sweeps <c>idempotency_records</c> for the given tenant, pruning records whose
    /// retention window has elapsed and whose linked operation (if any) is terminal.
    /// Emits one <c>audit_events</c> row per pruned record (<c>privacy.retention.idempotency_pruned</c>).
    /// Both prune and audit are committed atomically.
    /// </summary>
    /// <param name="tenantId">
    /// The tenant whose records to sweep.  The context's global query filter already scopes
    /// all queries to this tenant; the parameter is accepted explicitly so the audit subject
    /// is correct and the intent is clear to callers.
    /// </param>
    /// <param name="now">Reference clock value (injected for testability).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RetentionSweepResult> SweepIdempotencyRecordsAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Step 1: load candidates past their retention deadline (global filter scopes to tenant).
        var candidates = await db.IdempotencyRecords
            .Where(r => r.RetentionDeadline < now)
            .ToListAsync(ct);

        int pruned = 0;
        int skipped = 0;

        foreach (var record in candidates)
        {
            // Step 2: resolve linked operation status (AsNoTracking; may be null for orphan records).
            // The global tenant query filter scopes this to the swept tenant; the explicit
            // TenantId predicate is a second, load-bearing guard (do not remove either).
            var linkedOp = await db.BookingOperations
                .AsNoTracking()
                .Where(o => o.TenantId == tenantId && o.OperationId == record.OperationId)
                .Select(o => (Domain.Enums.OperationStatus?)o.Status)
                .FirstOrDefaultAsync(ct);

            // Step 3: policy check.
            if (!RetentionPolicy.ShouldPrune(record.RetentionDeadline, now, linkedOp))
            {
                skipped++;
                continue;
            }

            // Prune: remove the record.
            db.IdempotencyRecords.Remove(record);

            // Append audit event (spec 12 §6.3 "INSERT audit_events (privacy_action)").
            // Serialize via JsonSerializer so the jsonb is always well-formed even if
            // future fields carry characters needing escaping.
            var metadataJson = JsonSerializer.Serialize(new
            {
                reason = "retention_window_elapsed",
                retention_deadline = record.RetentionDeadline,
            });

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"audit-{Guid.NewGuid():N}",
                EventType = "privacy.retention.idempotency_pruned",
                Actor = "system",
                SubjectRef = record.IdempotencyKey,
                OperationId = record.OperationId,
                Metadata = metadataJson,
                OccurredAt = now,
            });

            pruned++;
        }

        // Step 4: atomic commit — prune + audit together.
        if (pruned > 0)
            await db.SaveChangesAsync(ct);

        return new RetentionSweepResult(pruned, skipped);
    }
}
