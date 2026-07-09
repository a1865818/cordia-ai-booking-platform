namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an action requires recorded customer consent that is absent or withdrawn
/// (spec 14 §24.2, Inv-13 fail-closed). Intended consumers: consent-gated reads/writes on
/// conversation transcripts and recordings (spec 13 session surfaces) and any handler that
/// must refuse to proceed until <c>consent.recorded</c> exists for the required scope.
/// Maps to HTTP 409 Conflict — the request is well-formed but conflicts with the subject's
/// current consent state; re-submit after consent is recorded.
/// </summary>
public sealed class ConsentRequiredException : DomainException
{
    /// <summary>The consent scope that is missing (e.g. "recording", "transcript").</summary>
    public string RequiredScope { get; }

    /// <summary>The subject the action targeted (e.g. a session or customer ref).</summary>
    public string SubjectRef { get; }

    public ConsentRequiredException(string requiredScope, string subjectRef)
        : base($"Consent scope '{requiredScope}' has not been recorded for '{subjectRef}'.")
    {
        RequiredScope = requiredScope;
        SubjectRef = subjectRef;
    }
}
