using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Read + consent-update access to <see cref="ConversationSession"/> rows.
/// The session row is CREATED by the channel gateway (spec 15/02, D12); this
/// repository only reads it and updates its consent_* columns (spec 14 §4.3).
/// </summary>
public interface IConversationSessionRepository
{
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
}
