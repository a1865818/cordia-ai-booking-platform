using MediatR;

namespace Cordia.Application.Handlers.Security.RecordConsent;

/// <summary>
/// Persists a captured recording-consent outcome onto an EXISTING conversation
/// session (spec 14 §5.1, D12 — the gateway creates the row; this command only
/// updates its consent_* columns). <paramref name="Outcome"/> carries the wire
/// value of the ratified consent_state pg-enum: granted | declined |
/// not_required | alternative_offered.
/// </summary>
public sealed record RecordConsentCommand(
    string TenantId,
    string SessionId,
    string Outcome,
    string ConsentPolicyVersion,
    string JurisdictionPolicyId,
    bool RecordingAllowed,
    DateTimeOffset CapturedAt,
    string NoticeChannel,
    string? CorrelationId) : IRequest<RecordConsentResult>;

/// <summary>Authoritative persisted result returned to the agent.</summary>
public sealed record RecordConsentResult(string SessionId, bool RecordingAllowed);
