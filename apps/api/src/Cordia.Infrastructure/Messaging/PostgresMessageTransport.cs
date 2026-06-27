using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// PostgreSQL job-store implementation of <see cref="IMessageTransport"/> (OD-1, §10, §12.3).
/// "Publish" is in-process dispatch to the registered consumer via <see cref="IQueueConsumerDispatcher"/>.
/// If no consumer is registered (empty registry in spec 05; spec 06 populates it), the message is
/// treated as delivered so the outbox publisher marks the row and does not wedge.
/// Genuine dispatch exceptions propagate so T3's publisher leaves the row unpublished for retry.
/// </summary>
public sealed class PostgresMessageTransport(
    IQueueConsumerDispatcher dispatcher,
    ILogger<PostgresMessageTransport> logger) : IMessageTransport
{
    public async Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
    {
        var dispatched = await dispatcher.DispatchAsync(envelope, queue, ct);

        if (!dispatched)
        {
            logger.LogInformation(
                "No consumer registered for message_type {MessageType} on queue {Queue}; " +
                "treating as delivered (consumers owned by spec 06). message_id={MessageId}",
                envelope.MessageType,
                LogicalQueueRegistry.WireName(queue),
                envelope.MessageId);
        }
        else
        {
            logger.LogDebug(
                "Dispatched message_id={MessageId} message_type={MessageType} to queue {Queue}",
                envelope.MessageId,
                envelope.MessageType,
                LogicalQueueRegistry.WireName(queue));
        }
    }
}
