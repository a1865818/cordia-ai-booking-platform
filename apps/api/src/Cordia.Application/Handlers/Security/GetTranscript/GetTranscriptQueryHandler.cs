using System.Globalization;
using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Security.GetTranscript;

/// <summary>
/// Enforces the §3.1 spine in order: (1) tenant scope is the controller's <c>TenantGuard</c>;
/// (2/3) grant path — <c>RequireGrantAsync(read_sensitive)</c> against the session's OWN
/// customer ref (the grant service deny-audits + throws 403 itself); role path — actor +
/// reason code + <see cref="StaffRole"/> operator (owner ok), failure deny-audited here and
/// thrown as 403. A caller presenting NEITHER credential is denied BEFORE the session is loaded
/// (mirrors GetModelTrace) so an unauthorized caller cannot tell an absent session (404) from a
/// present one (403/409) — no existence oracle. Authorization also runs before the consent check
/// so an unauthorized caller never learns the session's consent state. (4) consent —
/// <c>RecordingAllowed != true</c> ⇒
/// deny audit + <see cref="ConsentRequiredException"/> (409, the first thrower). (5) assemble
/// from the object store and append the ALLOW audit (<c>read_sensitive_transcript</c>,
/// data_classes [pii, transcript]) per §5.1.
/// </summary>
public sealed class GetTranscriptQueryHandler(
    IConversationSessionRepository sessions,
    IGrantAuthorizationService grants,
    IObjectStore store,
    IAuditWriter audit)
    : IRequestHandler<GetTranscriptQuery, TranscriptView>
{
    private const string Purpose = "transcript";
    private const string Action = "read_sensitive_transcript";

    public async Task<TranscriptView> Handle(GetTranscriptQuery query, CancellationToken ct)
    {
        // Auth-first (mirror GetModelTrace): decide the auth path from the QUERY and reject a
        // caller presenting neither a grant nor a staff role BEFORE loading the session, so a
        // caller with only tenant scope cannot distinguish a real session (403/409) from an absent
        // one (404) — no existence oracle (L5). A caller who DID present a credential may still
        // learn existence (404), exactly as GetModelTrace surfaces found:true/false to staff.
        var hasGrant = !string.IsNullOrWhiteSpace(query.GrantId);
        var hasStaffRole = !string.IsNullOrWhiteSpace(query.ActorId)
            && !string.IsNullOrWhiteSpace(query.ReasonCode)
            && StaffRole.Satisfies(query.ActorRole, StaffRole.Operator);
        if (!hasGrant && !hasStaffRole)
        {
            // Inv-11: session/customer identity alone never authorizes a sensitive read.
            await WriteDenyAsync(query, customerRef: null, reason: "no_grant_or_staff_role");
            throw new AuthorizationFailedException(
                Action,
                "Reading a transcript requires a read_sensitive verification grant or a staff role with a reason code (spec 14 §3.1).");
        }

        // Load the session as NULLABLE and do NOT 404 before authorizing: a caller presenting only a
        // non-empty (possibly bogus) grant_id must not be able to distinguish an absent session
        // (would-be 404) from a present one (403). The auth below decides existence-safely.
        var session = await sessions.GetAsync(query.TenantId, query.SessionId, ct);

        string actorType;
        string actorId;

        // Prefer the staff-role path when a valid staff role is present (B1). The query contract
        // permits BOTH a grant and staff credentials; taking the grant branch on presence alone
        // would deny an otherwise-authorized staff read whenever the grant is stale/wrong. A valid
        // staff role authorizes the read on its own, so a redundant (or bad) grant is never
        // enforced — the path is chosen by credential VALIDITY, not by grant presence.
        if (hasStaffRole)
        {
            // Staff/operator is a real, server-checked credential (like GetModelTrace): a missing
            // session is a truthful 404 for them.
            if (session is null)
                throw new ConversationSessionNotFoundException(query.TenantId, query.SessionId);
            actorType = query.ActorRole!;
            actorId = query.ActorId!;
        }
        else
        {
            // Grant path (§3.1 step 3, spec 08 consumed as-is): authorize against the session's OWN
            // customer ref. A MISSING session yields an EMPTY owner ref, which matches no grant, so
            // RequireGrantAsync deny-audits and throws 403 — identical to a bogus/mismatched grant on
            // a real session. A caller presenting only a non-empty but BOGUS grant_id therefore can no
            // longer tell an absent session (would-be 404) from a present one (403): the existence
            // oracle stays closed even against a credential-shaped grant. Success proves the session
            // exists and is owned by the grant's customer (an empty ref never matches a real grant).
            await grants.RequireGrantAsync(
                query.TenantId, query.GrantId!, session?.CustomerRef ?? string.Empty,
                VerificationActionEnum.ReadSensitive, targetBookingId: null,
                // A transcript read is a conversation_session resource, not a booking — so the
                // service's deny audit attributes the refusal to the right resource type.
                targetType: "conversation_session", ct: ct);
            actorType = "customer";
            actorId = session!.CustomerRef ?? "customer";
        }

        // (4) Consent AFTER authorization: recording was never permitted ⇒ there is no lawful
        // transcript to read — 409, and the refusal itself is on the trail. `session` is non-null
        // here — the staff path 404'd a missing one and the grant path authorized against a real
        // owner ref (a missing session's empty ref would have 403'd above).
        if (session!.RecordingAllowed != true)
        {
            await WriteDenyAsync(query, session.CustomerRef, reason: "consent_absent");
            throw new ConsentRequiredException("transcript", query.SessionId);
        }

        var turns = new List<TranscriptTurnView>();
        foreach (var id in await store.ListIdsAsync(query.TenantId, Purpose, $"{query.SessionId}.", ct))
        {
            ct.ThrowIfCancellationRequested();

            // Turn objects for THIS session are exactly "{sessionId}.{6 digits}". Because
            // session ids may legally contain dots, a sibling session id that extends this
            // one (e.g. "sess-1.retry" while reading "sess-1") also matches the prefix — its
            // ids carry a non-6-digit suffix and MUST be skipped, never parsed (a FormatException
            // 500) or spliced into this transcript as a phantom turn.
            var suffix = id[(query.SessionId.Length + 1)..];
            if (suffix.Length != 6
                || !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                continue;

            var bytes = await store.GetAsync(query.TenantId, Purpose, id, ct);
            if (bytes is null)
                continue; // expired-but-unswept objects are unreadable by design

            using var doc = JsonDocument.Parse(bytes);
            turns.Add(new TranscriptTurnView(
                Seq: seq,
                TurnId: doc.RootElement.GetProperty("turn_id").GetString() ?? string.Empty,
                Speaker: doc.RootElement.GetProperty("speaker").GetString() ?? string.Empty,
                Text: doc.RootElement.GetProperty("text").GetString() ?? string.Empty,
                Ts: doc.RootElement.GetProperty("ts").GetDateTimeOffset()));
        }

        // Guarantee seq order here (not just via the store's id sort) — the transcript's
        // meaning depends on turn order, so the reader owns the guarantee.
        turns.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        // (5) ALLOW audit for the sensitive read (§5.1: reads are audited like writes). Written on
        // CancellationToken.None (B2): an audit record must persist even if the client disconnects.
        await audit.WriteAsync(new AuditRecord(
            TenantId: query.TenantId,
            ActorType: actorType,
            ActorId: actorId,
            Action: Action,
            TargetType: "conversation_session",
            TargetId: query.SessionId,
            Decision: "allow",
            DataClasses: ["pii", "transcript"],
            ReasonCode: string.IsNullOrWhiteSpace(query.ReasonCode) ? null : query.ReasonCode,
            CorrelationId: query.SessionId,
            Extra: new
            {
                turn_count = turns.Count,
                access_path = hasStaffRole ? "staff_role" : "verification_grant",
            }), CancellationToken.None);

        return new TranscriptView(query.SessionId, turns);
    }

    // Deny audits are written on CancellationToken.None (B2) so a denial record is durable even
    // when the request is canceled by a client disconnect mid-authorization.
    private Task WriteDenyAsync(GetTranscriptQuery query, string? customerRef, string reason)
        => audit.WriteAsync(new AuditRecord(
            TenantId: query.TenantId,
            // Fallback convention (matches OperatorActionBehavior/GetModelTrace): an ASSERTED
            // but unknown role is recorded as "operator" with the raw value in context; only a
            // caller asserting NO role at all is presumed to be the customer/session subject.
            ActorType: StaffRole.IsKnown(query.ActorRole)
                ? query.ActorRole!
                : string.IsNullOrWhiteSpace(query.ActorRole) ? "customer" : "operator",
            ActorId: string.IsNullOrWhiteSpace(query.ActorId)
                ? customerRef ?? "unknown"
                : query.ActorId,
            Action: Action,
            TargetType: "conversation_session",
            TargetId: query.SessionId,
            Decision: "deny",
            DataClasses: ["pii", "transcript"],
            ReasonCode: string.IsNullOrWhiteSpace(query.ReasonCode) ? null : query.ReasonCode,
            CorrelationId: query.SessionId,
            Extra: new { error = reason }), CancellationToken.None);
}
