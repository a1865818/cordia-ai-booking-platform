using Cordia.Domain.Messaging;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for dispatching a <see cref="JobEnvelope"/> to its registered consumer handler (§3, OD-1).
/// Decouples <see cref="IMessageTransport"/> in Infrastructure from MediatR/Application.
/// </summary>
public interface IQueueConsumerDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="envelope"/> (routed to <paramref name="queue"/>) to its registered
    /// consumer handler. Returns <see langword="true"/> if a consumer handled it; <see langword="false"/>
    /// if none is registered (caller logs and treats as delivered — see §3 / OD-1, consumers are owned
    /// by spec 06).
    /// </summary>
    Task<bool> DispatchAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default);
}
