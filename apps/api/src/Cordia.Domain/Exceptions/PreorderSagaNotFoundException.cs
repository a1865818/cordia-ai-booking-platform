namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a pre-order saga action targets a saga that does not exist for the current tenant
/// (spec 11 §5.3). Maps to HTTP 404 Not Found.
/// </summary>
public sealed class PreorderSagaNotFoundException : DomainException
{
    /// <summary>The saga id that was not found for the current tenant.</summary>
    public string SagaId { get; }

    public PreorderSagaNotFoundException(string sagaId)
        : base($"Pre-order saga '{sagaId}' was not found.")
    {
        SagaId = sagaId;
    }
}
