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

    /// <summary>
    /// Counts intent revisions still holding guest contact data past the retention window.
    ///
    /// <para><b>This surveys; it does not erase — and that is the point.</b>
    /// <c>booking_intent_revisions.canonical_payload</c> is immutable and hashed: redacting it
    /// would make the stored payload stop re-hashing to <c>payload_hash</c>, and the whole
    /// idempotency chain (Inv-2/3) is derived from that hash. Whether an aged, terminal revision
    /// may be redacted anyway is a spec 12/14 ruling, not something a sweep should decide on its
    /// own.</para>
    ///
    /// <para>What it CAN do is stop the exposure being invisible. Before this, contact data sat
    /// in revisions permanently and nothing anywhere counted it, so "how much un-erasable guest
    /// data do we hold?" had no answer. Now it does — and the number is what makes the ruling
    /// actionable rather than theoretical.</para>
    ///
    /// <para>Only TERMINAL operations past the window are counted: a live operation's payload is
    /// still operationally needed, so it is not an erasure candidate under any policy.</para>
    /// </summary>
    /// <returns>
    /// <c>Pruned</c> is always 0 — nothing is erased. <c>Skipped</c> is the count of revisions
    /// holding contact data that retention cannot currently reach.
    /// </returns>
    public async Task<RetentionSweepResult> SurveyUnerasableContactAsync(
        string tenantId,
        DateTimeOffset now,
        TimeSpan? window = null,
        CancellationToken ct = default)
    {
        var effectiveWindow = window ?? RetentionPolicy.DefaultSessionAnonymizationWindow;
        var cutoff = now - effectiveWindow;

        // The terminal filter runs in SQL (`status = ANY(...)`), not after the fact. It used to sit
        // in the Count() below, so every aged operation for the tenant — live ones included — was
        // fetched WITH its full canonical payload just to be discarded in memory. On a tenant with
        // a large history that is hundreds of thousands of JSON blobs pulled into the sweeper on a
        // background timer, per tenant, per cycle, to produce a single integer.
        //
        // The payloads that remain are STREAMED rather than materialised: `CarriesContact` has to
        // parse the JSON, which cannot be pushed into the query without duplicating its rules in
        // SQL (and letting the two drift), but nothing needs two payloads in memory at once.
        var payloads = (
            from op in db.BookingOperations.IgnoreQueryFilters()
            join rev in db.BookingIntentRevisions.IgnoreQueryFilters()
                on new { op.TenantId, op.IntentId, Revision = op.IntentRevision }
                equals new { rev.TenantId, rev.IntentId, rev.Revision }
            where op.TenantId == tenantId
                && op.UpdatedAt < cutoff
                && RetentionPolicy.TerminalStatuses.Contains(op.Status)
            select rev.CanonicalPayload)
            .AsAsyncEnumerable();

        var holding = 0;
        await foreach (var payload in payloads.WithCancellation(ct))
        {
            if (CarriesContact(payload))
                holding++;
        }

        return new RetentionSweepResult(0, holding);
    }

    /// <summary>
    /// True when a canonical payload carries a guest contact value.
    ///
    /// Looks only at the <c>customer</c> node the submission contract defines (spec 04 §4.2), not
    /// at any string that happens to look like a phone — over-counting would make the number
    /// useless for the decision it exists to inform.
    /// </summary>
    internal static bool CarriesContact(string? canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(canonicalPayload);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("customer", out var customer)
                || customer.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            foreach (var field in new[] { "phone_e164", "email", "display_name" })
            {
                if (customer.TryGetProperty(field, out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed historical row is not evidence of contact data, and must not take the
            // sweep down.
            return false;
        }
    }

    /// <summary>
    /// Anonymizes ended conversation sessions past the retention window
    /// (spec 14 §24.3): <c>customer_ref</c> is tombstoned (nulled) — the row
    /// and its consent evidence are PRESERVED for security audit; nothing is
    /// hard-deleted. Idempotent: already-anonymized rows are not candidates.
    /// One <c>privacy.retention.session_anonymized</c> audit row per session.
    /// Authoritative on <paramref name="tenantId"/> (IgnoreQueryFilters +
    /// explicit predicate), same as the idempotency sweep.
    /// </summary>
    public async Task<RetentionSweepResult> SweepConversationSessionsAsync(
        string tenantId,
        DateTimeOffset now,
        TimeSpan? window = null,
        CancellationToken ct = default)
    {
        var effectiveWindow = window ?? RetentionPolicy.DefaultSessionAnonymizationWindow;
        var cutoff = now - effectiveWindow;

        var candidates = await db.ConversationSessions
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId
                && s.EndedAt != null && s.EndedAt < cutoff
                && s.CustomerRef != null)
            .ToListAsync(ct);

        var anonymized = 0;
        foreach (var session in candidates)
        {
            if (!RetentionPolicy.ShouldAnonymizeSession(session.EndedAt, now, effectiveWindow))
                continue;

            session.CustomerRef = null;
            session.Version++;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"audit-{Guid.NewGuid():N}",
                EventType = "privacy.retention.session_anonymized",
                Actor = "system",
                SubjectRef = session.SessionId,
                Metadata = JsonSerializer.Serialize(new
                {
                    reason = "retention_window_elapsed",
                    ended_at = session.EndedAt,
                    window_days = effectiveWindow.TotalDays,
                }),
                OccurredAt = now,
            });

            anonymized++;
        }

        if (anonymized > 0)
            await db.SaveChangesAsync(ct);

        return new RetentionSweepResult(anonymized, candidates.Count - anonymized);
    }

    /// <summary>
    /// Physically prunes verification grants past the retention window
    /// (spec 14 §24.3; grant lifecycle owned by spec 08): only CONSUMED or
    /// EXPIRED grants are eligible — an active grant is never touched. One
    /// <c>privacy.retention.grant_pruned</c> audit row per pruned grant.
    /// This is the only sanctioned physical-deletion path for
    /// <c>verification_grants</c>.
    /// </summary>
    public async Task<RetentionSweepResult> SweepVerificationGrantsAsync(
        string tenantId,
        DateTimeOffset now,
        TimeSpan? window = null,
        CancellationToken ct = default)
    {
        var effectiveWindow = window ?? RetentionPolicy.DefaultGrantPruneWindow;
        var cutoff = now - effectiveWindow;

        var candidates = await db.VerificationGrants
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId
                && ((g.ConsumedAt != null && g.ConsumedAt < cutoff) || g.ExpiresAt < cutoff))
            .ToListAsync(ct);

        var pruned = 0;
        foreach (var grant in candidates)
        {
            if (!RetentionPolicy.ShouldPruneGrant(grant.ExpiresAt, grant.ConsumedAt, now, effectiveWindow))
                continue;

            db.VerificationGrants.Remove(grant);

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"audit-{Guid.NewGuid():N}",
                EventType = "privacy.retention.grant_pruned",
                Actor = "system",
                SubjectRef = grant.GrantId,
                Metadata = JsonSerializer.Serialize(new
                {
                    reason = "retention_window_elapsed",
                    expires_at = grant.ExpiresAt,
                    consumed = grant.ConsumedAt != null,
                    window_days = effectiveWindow.TotalDays,
                }),
                OccurredAt = now,
            });

            pruned++;
        }

        if (pruned > 0)
            await db.SaveChangesAsync(ct);

        return new RetentionSweepResult(pruned, candidates.Count - pruned);
    }
}
