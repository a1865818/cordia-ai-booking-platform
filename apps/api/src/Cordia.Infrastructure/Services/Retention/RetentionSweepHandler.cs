using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Services.Retention;

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
    /// The tenant whose records to sweep. This is a background-job entry point and is
    /// AUTHORITATIVE on this argument: every query bypasses the ambient context's tenant
    /// filter (<c>IgnoreQueryFilters</c>) and re-asserts <c>TenantId == tenantId</c>, so the
    /// candidate scope, the linked-operation lookup, and the audit rows are all keyed to the
    /// SAME tenant. A worker whose context is scoped to a different tenant (or unset) can
    /// therefore never mis-classify another tenant's record as an orphan and prune it.
    /// </param>
    /// <param name="now">Reference clock value (injected for testability).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RetentionSweepResult> SweepIdempotencyRecordsAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Step 1: load candidates past their retention deadline for the REQUESTED tenant.
        // Authoritative on tenantId (IgnoreQueryFilters + explicit predicate) so the candidate
        // scope matches the linked-operation lookup below — preventing a context/argument
        // mismatch from loading one tenant's records and pruning them under another (P1).
        var candidates = await db.IdempotencyRecords
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.RetentionDeadline < now)
            .ToListAsync(ct);

        int pruned = 0;
        int skipped = 0;

        foreach (var record in candidates)
        {
            // Step 2: resolve linked operation status for the SAME requested tenant. Authoritative
            // on tenantId (IgnoreQueryFilters + explicit predicate) so an ambient context scoped
            // to a different tenant cannot null-out this lookup and mis-prune a non-orphan (P1).
            // A genuine null here means a true orphan (no operation for this tenant+id).
            var linkedOp = await db.BookingOperations
                .AsNoTracking()
                .IgnoreQueryFilters()
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
