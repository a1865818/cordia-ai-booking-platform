namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when the envelope carries an unknown or incompatible <c>message_type</c> schema
/// version that this consumer cannot process (spec 05 §8, §10).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class UnsupportedSchemaVersionException : DomainException
{
    public UnsupportedSchemaVersionException(string message) : base(message) { }
}
