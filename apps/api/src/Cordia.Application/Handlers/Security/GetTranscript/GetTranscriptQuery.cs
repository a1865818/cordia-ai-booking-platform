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

/// <summary>One persisted, redacted transcript turn.</summary>
public sealed record TranscriptTurnView(int Seq, string TurnId, string Speaker, string Text, DateTimeOffset Ts);

/// <summary>The assembled transcript (turns in seq order; expired objects already pruned/unreadable).</summary>
public sealed record TranscriptView(string SessionId, IReadOnlyList<TranscriptTurnView> Turns);

/// <summary>Structural rules only — authorization itself is the handler's §3.1 spine.</summary>
public sealed class GetTranscriptValidator : AbstractValidator<GetTranscriptQuery>
{
    public GetTranscriptValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
    }
}
