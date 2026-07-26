using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Create + consent-update access to conversation_sessions (spec 15 D12, spec 14 §4.3).
/// <see cref="CreateAsync"/> is the single-writer insert at channel ingress; the consent gate only
/// UPDATEs the consent_* columns.
/// </summary>
public class ConversationSessionRepository(ApplicationDbContext db) : IConversationSessionRepository
{
    // PostgreSQL unique_violation — a concurrent/retried insert of the same (tenant_id, session_id).
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Inserts the session row (spec-15 D12 single writer). Idempotent: a unique-violation on the
    /// <c>(tenant_id, session_id)</c> PK (reconnect / retry re-resolving the same session, spec-15
    /// §7 reconnect row) is swallowed and the already-persisted row is returned instead of throwing.
    /// <para>
    /// Invariant: a failed insert is NEVER left tracked on the shared request-scoped
    /// <see cref="ApplicationDbContext"/> — on ANY failure (unique-violation or otherwise) the added
    /// entity is detached before returning/rethrowing. The fail-closed <c>tenant.resolution</c> audit
    /// writes the <c>session_insert_failed</c> row on the same context; a still-tracked failed insert
    /// would be re-attempted by that <c>SaveChangesAsync</c> — either losing the audit row or silently
    /// inserting a session for a resolution that returned no context.
    /// </para>
    /// </summary>
    public async Task<ConversationSession> CreateAsync(
        ConversationSession session,
        CancellationToken ct = default)
    {
        try
        {
            db.ConversationSessions.Add(session);
            await db.SaveChangesAsync(ct);
            return session;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Idempotent create: the row already exists for this (tenant_id, session_id). Detach the
            // failed insert and return the persisted row so the caller sees a successful create.
            db.Entry(session).State = EntityState.Detached;
            return await db.ConversationSessions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .FirstAsync(s => s.TenantId == session.TenantId && s.SessionId == session.SessionId, ct);
        }
        catch
        {
            // Any other failure (FK 23503, not-null 23502, check 23514, deadlock 40P01, serialization
            // 40001, DbUpdateConcurrencyException, connection drop): never leave the failed insert
            // tracked on the shared request-scoped DbContext. The fail-closed tenant.resolution audit
            // writes on the same context and would otherwise re-attempt this insert (losing the audit
            // row or inserting a session for a resolution that returned no context). Detach + rethrow.
            db.Entry(session).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<ConversationSession?> GetAsync(
        string tenantId,
        string sessionId,
        CancellationToken ct = default)
        => await db.ConversationSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SessionId == sessionId, ct);

    public async Task<int> RecordConsentAsync(
        string tenantId,
        string sessionId,
        ConsentState consentState,
        string consentPolicyVersion,
        string jurisdictionPolicyId,
        bool recordingAllowed,
        DateTimeOffset recordedAt,
        CancellationToken ct = default)
        => await db.ConversationSessions
            .Where(s => s.TenantId == tenantId && s.SessionId == sessionId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ConsentState, consentState)
                    .SetProperty(x => x.ConsentPolicyVersion, consentPolicyVersion)
                    .SetProperty(x => x.JurisdictionPolicyId, jurisdictionPolicyId)
                    .SetProperty(x => x.RecordingAllowed, recordingAllowed)
                    .SetProperty(x => x.ConsentRecordedAt, recordedAt)
                    .SetProperty(x => x.Version, x => x.Version + 1),
                ct);
}
