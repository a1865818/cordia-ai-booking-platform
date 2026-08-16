using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;

namespace Cordia.Application.Handlers.Security.AppendTranscriptTurn;

/// <summary>
/// The spec-14 transcript producer (§4.3 object-store row): loads the session (tenant-scoped,
/// D12 — the session row itself is never written here except for the redacted list-row
/// <c>Preview</c> snippet), enforces the consent gate
/// (<c>RecordingAllowed == true</c> or nothing persists), re-sanitizes the already-redacted
/// text (Inv-12 defense-in-depth: the agent runs <c>redact_text</c> pre-emit; this lane runs
/// <c>SensitiveDataMask.Sanitize</c> pre-persist), and stores one encrypted object per turn —
/// <c>{sessionId}.{seq:D6}</c>, purpose <c>"transcript"</c>, 90d TTL applied by the store
/// (GD-3) and swept by the retention service. After a successful persist the session's
/// <c>Preview</c> is updated to the sanitized text truncated to 280 characters so the
/// conversation list shows the latest turn. The session's first persisted turn appends a
/// <c>transcript.recording_started</c> audit event (AT-LEAST-ONCE: the emptiness check and the
/// put are not atomic, so concurrent first turns may rarely duplicate the marker — consistent
/// with the platform's at-least-once audit posture; consumers must not assume exactly one).
/// Per-turn audit rows are deliberately not written (the turn objects themselves are the
/// consented data plane — the trail is consent.recorded + recording_started + the reader's
/// read_sensitive_transcript audits).
/// </summary>
public sealed class AppendTranscriptTurnCommandHandler(
    IConversationSessionRepository sessions,
    IObjectStore store,
    IAuditWriter audit)
    : IRequestHandler<AppendTranscriptTurnCommand, TranscriptTurnPersistedResult>
{
    public const string Purpose = "transcript";

    public const int PreviewMaxLength = 280;

    public async Task<TranscriptTurnPersistedResult> Handle(
        AppendTranscriptTurnCommand command, CancellationToken ct)
    {
        var session = await sessions.GetAsync(command.TenantId, command.SessionId, ct)
            ?? throw new ConversationSessionNotFoundException(command.TenantId, command.SessionId);

        // §5.2 fail-closed: absent/unknown/declined consent ⇒ transcript suppressed, no object.
        if (session.RecordingAllowed != true)
            return new TranscriptTurnPersistedResult(command.SessionId, command.Seq, Persisted: false);

        var now = DateTimeOffset.UtcNow;
        var sanitized = SensitiveDataMask.Sanitize(command.Text);
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            turn_id = command.TurnId,
            speaker = command.Speaker,
            // Inv-12 defense-in-depth — the agent already redacted; mask again at the boundary.
            text = sanitized,
            ts = now,
        });

        var prefix = $"{command.SessionId}.";
        var isFirstTurn = (await store.ListIdsAsync(command.TenantId, Purpose, prefix, ct)).Count == 0;

        await store.PutAsync(
            command.TenantId, Purpose,
            $"{command.SessionId}.{command.Seq.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}",
            content, ttl: null /* store applies the GD-3 transcript default */, ct);

        await sessions.SetPreviewAsync(command.TenantId, command.SessionId, TruncatePreview(sanitized), ct);

        if (isFirstTurn)
        {
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: "agent",
                ActorId: "agent",
                Action: "transcript.recording_started",
                TargetType: "conversation_session",
                TargetId: command.SessionId,
                Decision: "allow",
                DataClasses: ["transcript"],
                CorrelationId: command.SessionId,
                OccurredAt: now,
                Extra: new { first_turn_id = command.TurnId, first_seq = command.Seq }), ct);
        }

        return new TranscriptTurnPersistedResult(command.SessionId, command.Seq, Persisted: true);
    }

    internal static string TruncatePreview(string sanitized)
        => sanitized.Length <= PreviewMaxLength ? sanitized : sanitized[..PreviewMaxLength];
}
