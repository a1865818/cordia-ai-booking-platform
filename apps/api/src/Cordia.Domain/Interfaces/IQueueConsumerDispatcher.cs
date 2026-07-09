using Cordia.Domain.Messaging;

namespace Cordia.Domain.Interfaces;

/// <summary>Outcome of dispatching a <see cref="JobEnvelope"/> to the consumer registry.</summary>
public enum DispatchResult
{
    /// <summary>A registered consumer handled the message.</summary>
    Dispatched,

    /// <summary>
    /// The message type is deliberately emitted without a consumer (audit trail / future consumer);
    /// the transport treats it as delivered.
    /// </summary>
    IntentionallyUnconsumed,

    /// <summary>
    /// No consumer AND not declared intentionally unconsumed — a producer/registration defect.
    /// The transport surfaces this loudly (dead-letter) instead of silently dropping the message.
    /// </summary>
    NoConsumer,
}

/// <summary>
/// Domain seam for dispatching a <see cref="JobEnvelope"/> to its registered consumer handler (§3, OD-1).
/// Decouples <see cref="IMessageTransport"/> in Infrastructure from MediatR/Application.
/// </summary>
public interface IQueueConsumerDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="envelope"/> (routed to <paramref name="queue"/>) to its registered
    /// consumer handler. See <see cref="DispatchResult"/> for the three outcomes — an undeclared
    /// missing consumer is NOT silently treated as delivered (spec 11 review root cause: dropped
    /// waitlist-offer / preorder-alternatives notifications).
    /// </summary>
    Task<DispatchResult> DispatchAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default);
}
