namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when an optimistic-concurrency UPDATE returns 0 rows affected — a concurrent worker
/// advanced the row before this worker could commit its transition (§22.1, spec 06 §7).
/// Maps to HTTP 409 Conflict.
/// </summary>
public sealed class OperationConflictException : DomainException
{
    /// <summary>The operation ID for which the transition was lost.</summary>
    public string OperationId { get; }

    public OperationConflictException(string operationId, string message)
        : base(message)
    {
        OperationId = operationId;
    }
}
