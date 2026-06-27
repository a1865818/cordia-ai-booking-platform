namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a submission arrives with an idempotency key that is already bound
/// to a different canonical request hash (spec 04 §5.1, Inv-3).
/// Maps to HTTP 409 Conflict.
///
/// NOTE: Spec 04 is the SOLE definer of this exception — spec 03/08 reference it, never redefine it.
/// </summary>
public sealed class IdempotencyConflictException : DomainException
{
    /// <summary>The idempotency key that is already bound.</summary>
    public string IdempotencyKey { get; }

    /// <summary>The operation already bound to this key.</summary>
    public string ExistingOperationId { get; }

    public IdempotencyConflictException(string idempotencyKey, string existingOperationId, string message)
        : base(message)
    {
        IdempotencyKey = idempotencyKey;
        ExistingOperationId = existingOperationId;
    }
}
