namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a message cannot be processed due to an invalid shape, violated invariant,
/// or unrecoverable structural defect (spec 05 §8, §10).
/// Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public class PoisonMessageException : DomainException
{
    public PoisonMessageException(string message) : base(message) { }
}

/// <summary>
/// Thrown by the transport when a ROUTABLE message type has no registered consumer and is not
/// declared intentionally unconsumed — a permanent registration defect the outbox publisher /
/// scheduled-job poller dead-letter immediately. Distinct from its base so a consumer-thrown
/// <see cref="PoisonMessageException"/> (malformed payload etc.) is never mislabelled as
/// "unconsumed_message_type" — that keeps its ordinary retry-then-dead-letter path.
/// </summary>
public sealed class UnconsumedMessageTypeException : PoisonMessageException
{
    public UnconsumedMessageTypeException(string message) : base(message) { }
}
