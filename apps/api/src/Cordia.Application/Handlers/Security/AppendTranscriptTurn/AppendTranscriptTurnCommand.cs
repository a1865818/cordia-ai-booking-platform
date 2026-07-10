using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Security.AppendTranscriptTurn;

/// <summary>
/// Persists ONE redacted transcript turn for a conversation session into the encrypted object
/// store (spec 14 §4.3 / §5.1–§5.2; capture originates in the spec 01/02 streaming pipeline).
/// Consent-gated fail-closed: when the session's <c>recording_allowed</c> is not exactly
/// <c>true</c> (absent, unknown, or declined) NOTHING is persisted and the command settles with
/// <c>Persisted = false</c> — §5.2 "transcript suppressed" is a normal outcome for the producer,
/// never an error (the consent-absence 409 belongs to the READER). Inv-1: this command persists
/// only — it emits no queue messages and mutates no business state. Idempotent per
/// (session, seq): a re-post overwrites the same object.
/// </summary>
public sealed record AppendTranscriptTurnCommand(
    string TenantId,
    string SessionId,
    int Seq,
    string TurnId,
    string Speaker,
    string Text) : IRequest<TranscriptTurnPersistedResult>;

/// <summary>Producer outcome: whether the turn was persisted (false ⇒ consent suppressed it).</summary>
public sealed record TranscriptTurnPersistedResult(string SessionId, int Seq, bool Persisted);

/// <summary>Structural rules; the consent gate itself lives in the handler (data state, not shape).</summary>
public sealed class AppendTranscriptTurnValidator : AbstractValidator<AppendTranscriptTurnCommand>
{
    public AppendTranscriptTurnValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.Seq).GreaterThanOrEqualTo(0);
        RuleFor(x => x.TurnId).NotEmpty();
        RuleFor(x => x.Speaker).NotEmpty()
            .Must(s => s is "customer" or "agent")
            .WithMessage("Speaker must be 'customer' or 'agent'.");
        RuleFor(x => x.Text).NotEmpty();
    }
}
