namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an operator/owner action fails its authorization preconditions — a missing actor
/// identity or reason code (spec 14 §24.5), or a future role check (the tracked 08/14 open
/// decision). Fail-closed: the action is refused BEFORE any effect runs.
/// Maps to HTTP 403 Forbidden.
/// </summary>
public sealed class AuthorizationFailedException : DomainException
{
    /// <summary>The audited action that was refused (e.g. "catalog.published").</summary>
    public string ActionName { get; }

    public AuthorizationFailedException(string actionName, string message)
        : base(message)
    {
        ActionName = actionName;
    }
}
