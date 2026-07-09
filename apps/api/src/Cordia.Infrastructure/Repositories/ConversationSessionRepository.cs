using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Consent-update access to conversation_sessions (spec 14 §4.3, D12).
/// The row is created by the channel gateway; this repository never inserts.
/// </summary>
public class ConversationSessionRepository(ApplicationDbContext db) : IConversationSessionRepository
{
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
