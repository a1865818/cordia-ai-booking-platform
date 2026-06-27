namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when the submitted <c>confirmation.intent_payload_hash</c> does not equal
/// the API-computed canonical revision hash (spec 04 §8.8, Inv-1).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class ConfirmationHashMismatchException : DomainException
{
    public ConfirmationHashMismatchException(string message) : base(message) { }
}
