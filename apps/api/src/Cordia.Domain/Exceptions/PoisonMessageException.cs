namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a message cannot be processed due to an invalid shape, violated invariant,
/// or unrecoverable structural defect (spec 05 §8, §10).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class PoisonMessageException : DomainException
{
    public PoisonMessageException(string message) : base(message) { }
}
