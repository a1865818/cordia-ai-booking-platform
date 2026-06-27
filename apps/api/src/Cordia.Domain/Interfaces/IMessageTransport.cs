using Cordia.Domain.Messaging;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Pluggable transport seam for durable message delivery (§3, §10, OD-1).
/// Implementations may be a PostgreSQL job-store or an external broker; the
/// caller is decoupled from the physical delivery mechanism.
/// </summary>
public interface IMessageTransport
{
    /// <summary>
    /// Publishes <paramref name="envelope"/> to its logical <paramref name="queue"/> (at-least-once).
    /// Implementations are responsible for durable hand-off / delivery; the caller (outbox publisher)
    /// marks the row published only after this completes without throwing.
    /// </summary>
    Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default);
}
