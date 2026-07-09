using Cordia.Application.Common.Audit;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;
using DomainConsentState = Cordia.Domain.Enums.ConsentState;

namespace Cordia.Application.Handlers.Security.RecordConsent;

/// <summary>
/// Updates the session's consent_* columns and appends the consent audit event
/// in ONE transaction (spec 14 §3.1 step 5 — audit within the same txn as the
/// effect). 0 rows updated ⇒ the session does not exist for this tenant ⇒ 404
/// (the gateway is the single row creator, D12; consent never creates rows).
///
/// HTTP handler: owns its own transaction via IUnitOfWork (same pattern as
/// RecordDeliveryReceiptCommandHandler; inbox consumers must not open one).
/// Re-recording is allowed by design (reconnect re-runs the gate, spec 14 §8)
/// — the update is last-write-wins with a version bump.
/// </summary>
public sealed class RecordConsentCommandHandler(
    IConversationSessionRepository sessions,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<RecordConsentCommand, RecordConsentResult>
{
    internal static readonly IReadOnlyDictionary<string, DomainConsentState> OutcomeMap =
        new Dictionary<string, DomainConsentState>(StringComparer.Ordinal)
        {
            ["granted"] = DomainConsentState.Granted,
            ["declined"] = DomainConsentState.Declined,
            ["not_required"] = DomainConsentState.NotRequired,
            ["alternative_offered"] = DomainConsentState.AlternativeOffered,
        };

    public async Task<RecordConsentResult> Handle(RecordConsentCommand command, CancellationToken ct)
    {
        var outcome = OutcomeMap[command.Outcome];

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var rows = await sessions.RecordConsentAsync(
                command.TenantId,
                command.SessionId,
                outcome,
                command.ConsentPolicyVersion,
                command.JurisdictionPolicyId,
                command.RecordingAllowed,
                command.CapturedAt,
                ct2);

            if (rows == 0)
                throw new ConversationSessionNotFoundException(command.TenantId, command.SessionId);

            // Audit within the same transaction as the effect (spec 14 §3.1/§6).
            // Metadata is structured-only (no transcript text, no PII).
            // Actor is the AGENT (the software recording on the customer's
            // behalf — the true acting principal; there is no authenticated
            // customer identity here). The customer's decision is the outcome
            // field in the metadata, not the actor column.
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: "agent",
                    ActorId: "agent",
                    Action: "consent.recorded",
                    TargetType: "conversation_session",
                    TargetId: command.SessionId,
                    Decision: "allow",
                    DataClasses: ["consent"],
                    CorrelationId: command.CorrelationId,
                    Extra: new
                    {
                        outcome = command.Outcome,
                        consent_policy_version = command.ConsentPolicyVersion,
                        jurisdiction_policy_id = command.JurisdictionPolicyId,
                        recording_allowed = command.RecordingAllowed,
                        notice_channel = command.NoticeChannel,
                        captured_at = command.CapturedAt,
                    }),
                ct2);
        }, ct);

        return new RecordConsentResult(command.SessionId, command.RecordingAllowed);
    }
}
