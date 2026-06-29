namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a submission fails a tenant policy or semantic validation check
/// (not a structural/field validation failure — those are handled by FluentValidation → 400).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class IntentPolicyViolationException : DomainException
{
    public IntentPolicyViolationException(string message) : base(message) { }
}
