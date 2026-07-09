using System.Text.Json;
using Cordia.Domain.Entities;
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
    IAuditEventRepository auditRepo,
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
        var now = DateTimeOffset.UtcNow;

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
            await auditRepo.AddAsync(
                new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "consent.recorded",
                    Actor = "agent",
                    SubjectRef = command.SessionId,
                    CorrelationId = command.CorrelationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        actor_type = "customer",
                        action = "consent.recorded",
                        target_type = "conversation_session",
                        decision = "allow",
                        data_classes = new[] { "consent" },
                        outcome = command.Outcome,
                        consent_policy_version = command.ConsentPolicyVersion,
                        jurisdiction_policy_id = command.JurisdictionPolicyId,
                        recording_allowed = command.RecordingAllowed,
                        notice_channel = command.NoticeChannel,
                        captured_at = command.CapturedAt,
                    }),
                    OccurredAt = now,
                },
                ct2);
        }, ct);

        return new RecordConsentResult(command.SessionId, command.RecordingAllowed);
    }
}
