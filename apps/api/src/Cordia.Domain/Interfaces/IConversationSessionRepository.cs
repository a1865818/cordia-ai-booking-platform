using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Create + read + consent-update access to <see cref="ConversationSession"/> rows.
/// The session row is CREATED by the channel gateway (spec 15/02, D12) — this repository's
/// <see cref="CreateAsync"/> is that single-writer insert path; the consent gate (spec 14 §4.3)
/// only UPDATEs the consent_* columns and never inserts.
/// </summary>
public interface IConversationSessionRepository
{
    /// <summary>
    /// Single-writer insert of a new session at channel ingress (spec-15 D12). Idempotent on the
    /// <c>(tenant_id, session_id)</c> primary key: a duplicate insert (reconnect / retry re-resolving
    /// the same session) is treated as success and the already-persisted row is returned rather than
    /// throwing. Returns the persisted row (freshly inserted or the pre-existing one).
    /// </summary>
    Task<ConversationSession> CreateAsync(
        ConversationSession session,
        CancellationToken ct = default);

    /// <summary>Read-only fetch (AsNoTracking) of a session by id.</summary>
    Task<ConversationSession?> GetAsync(
        string tenantId,
        string sessionId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically updates the consent_* columns and bumps the version token.
    /// Returns the number of rows updated: 0 ⇒ no such session for this tenant.
    /// </summary>
    Task<int> RecordConsentAsync(
        string tenantId,
        string sessionId,
        ConsentState consentState,
        string consentPolicyVersion,
        string jurisdictionPolicyId,
        bool recordingAllowed,
        DateTimeOffset recordedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the redacted list-row snippet. Returns rows updated: 0 ⇒ no such session for this tenant.
    /// </summary>
    Task<int> SetPreviewAsync(
        string tenantId,
        string sessionId,
        string? preview,
        CancellationToken ct = default);
}
