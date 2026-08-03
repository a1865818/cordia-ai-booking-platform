using Cordia.Domain.Enums;
using System.Text.Json.Serialization;
using Cordia.Domain.Security;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Security.GetTranscript;

/// <summary>
/// Consent-gated sensitive read of a session's persisted transcript (spec 14 §3.1/§5.1 — the
/// full authorization spine, and the FIRST <c>ConsentRequiredException</c> call site).
/// Two authorization paths (§3.1 step 3): a spec-08 verification grant
/// (<c>read_sensitive</c>, scoped to the session's customer) OR a staff role satisfying
/// <c>operator</c> (owner ok, GD-1) with actor + reason code. Session identity alone NEVER
/// reads (Inv-11). Consent absent/declined ⇒ 409; every allow AND deny is audited.
/// </summary>
public sealed record GetTranscriptQuery(
    string TenantId,
    string SessionId,
    string? GrantId,
    string? ActorId,
    string? ActorRole,
    string? ReasonCode) : IRequest<TranscriptView>;

/// <summary>One persisted, redacted transcript turn (snake_case wire shape — the Python
/// tool contracts parse these keys).</summary>
public sealed record TranscriptTurnView(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("turn_id")] string TurnId,
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts);

/// <summary>The assembled transcript (turns in seq order; expired objects already pruned/unreadable).</summary>
public sealed record TranscriptView(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("turns")] IReadOnlyList<TranscriptTurnView> Turns);

/// <summary>Structural rules only — authorization itself is the handler's §3.1 spine.</summary>
public sealed class GetTranscriptValidator : AbstractValidator<GetTranscriptQuery>
{
    public GetTranscriptValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Path-segment shape rejected here as 400, not the store's fail-closed 500.
        RuleFor(x => x.SessionId).NotEmpty()
            .Must(ObjectStoreSegment.IsValid)
            .WithMessage("SessionId contains characters not allowed in a storage path segment.");
        // A *present* purpose must be one the audit trail can reason about (spec 14 §2). Absence is
        // deliberately NOT handled here: the handler's §3.1 spine already rejects a missing reason
        // with a deny-audited 403, and moving that to a 400 would drop the audit record.
        RuleFor(x => x.ReasonCode)
            .Must(SensitiveReadPurpose.IsRatified)
            .When(x => !string.IsNullOrWhiteSpace(x.ReasonCode))
            .WithMessage(
                "ReasonCode must be one of: " + string.Join(", ", SensitiveReadPurpose.All) + ".");
    }
}
