using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.Security.RecordConsent;

/// <summary>
/// Persists a captured recording-consent outcome onto an EXISTING conversation
/// session (spec 14 §5.1, D12 — the gateway creates the row; this command only
/// updates its consent_* columns). <paramref name="Outcome"/> carries the wire
/// value of the ratified consent_state pg-enum: granted | declined |
/// not_required | alternative_offered. Build from the generated wire contract
/// via <see cref="FromContract"/> so the enum→wire mapping lives in ONE place
/// (this layer), not in controllers.
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
    string? CorrelationId) : IRequest<RecordConsentResult>
{
    private static readonly IReadOnlyDictionary<Outcome, string> OutcomeWire =
        new Dictionary<Outcome, string>
        {
            [Domain.Generated.Outcome.Granted] = "granted",
            [Domain.Generated.Outcome.Declined] = "declined",
            [Domain.Generated.Outcome.NotRequired] = "not_required",
            [Domain.Generated.Outcome.AlternativeOffered] = "alternative_offered",
        };

    // Phase-1 regen merged the notice_channel enum into the shared TenantChannelEnum
    // (same wire values); SessionConsentState.NoticeChannel is now TenantChannelEnum.
    private static readonly IReadOnlyDictionary<TenantChannelEnum, string> NoticeChannelWire =
        new Dictionary<TenantChannelEnum, string>
        {
            [Domain.Generated.TenantChannelEnum.Voice] = "voice",
            [Domain.Generated.TenantChannelEnum.Web] = "web",
            [Domain.Generated.TenantChannelEnum.Messaging] = "messaging",
            [Domain.Generated.TenantChannelEnum.Email] = "email",
        };

    /// <summary>Builds the command from the generated cross-service contract.</summary>
    public static RecordConsentCommand FromContract(
        string tenantId,
        string sessionId,
        SessionConsentState consent,
        string? correlationId)
        => new(
            tenantId,
            sessionId,
            OutcomeWire[consent.Outcome],
            consent.ConsentPolicyVersion,
            consent.JurisdictionPolicyId,
            consent.RecordingAllowed,
            consent.CapturedAt,
            NoticeChannelWire[consent.NoticeChannel],
            correlationId);
}

/// <summary>Authoritative persisted result returned to the agent.</summary>
public sealed record RecordConsentResult(string SessionId, bool RecordingAllowed);
