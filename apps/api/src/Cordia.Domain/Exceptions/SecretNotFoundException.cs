namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a required provider credential is absent from the secret store
/// (spec 14 §24.4). The message carries the secret NAME only — never a value —
/// and is deliberately unmapped in the GlobalExceptionHandler (generic 500):
/// secret configuration is not client-visible.
/// </summary>
public sealed class SecretNotFoundException(string name)
    : DomainException($"Secret '{name}' is not configured in the server-side secret store.")
{
    public string Name { get; } = name;
}
