namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a replay request targets a dead-letter row that does not exist
/// (spec 05 §10, T7 replay handler).
/// Maps to HTTP 404 Not Found.
/// </summary>
public sealed class DeadLetterNotFoundException : DomainException
{
    public DeadLetterNotFoundException(string message) : base(message) { }
}
