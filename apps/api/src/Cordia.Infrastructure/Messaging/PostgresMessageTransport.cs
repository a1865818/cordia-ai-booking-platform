using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// PostgreSQL job-store implementation of <see cref="IMessageTransport"/> (OD-1, §10, §12.3).
/// "Publish" is in-process dispatch to the registered consumer via <see cref="IQueueConsumerDispatcher"/>.
/// A message type DECLARED intentionally unconsumed is treated as delivered so the outbox publisher
/// marks the row; a routable type with no consumer and no declaration is a registration defect and
/// throws <see cref="PoisonMessageException"/> so the publisher dead-letters it loudly instead of
/// silently dropping it (spec 11 review root cause — dropped waitlist-offer notifications).
/// Genuine dispatch exceptions propagate so T3's publisher leaves the row unpublished for retry.
/// </summary>
public sealed class PostgresMessageTransport(
    IQueueConsumerDispatcher dispatcher,
    TenantContext tenantContext,
    ILogger<PostgresMessageTransport> logger) : IMessageTransport
{
    public async Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
    {
        // Establish the scoped tenant from the envelope BEFORE dispatching. Background scopes
        // (outbox publisher / scheduled poller / step sweeper) have no tenant-resolution middleware,
        // so ITenantContext.TenantId would be empty and any consumer that reads through the
        // tenant-filtered ApplicationDbContext (e.g. via repositories) would query tenant "" and
        // find nothing. The envelope's tenant_id is authoritative for the message it carries.
        tenantContext.TenantId = envelope.TenantId;

        var result = await dispatcher.DispatchAsync(envelope, queue, ct);

        switch (result)
        {
            case DispatchResult.Dispatched:
                logger.LogDebug(
                    "Dispatched message_id={MessageId} message_type={MessageType} to queue {Queue}",
                    envelope.MessageId,
                    envelope.MessageType,
                    LogicalQueueRegistry.WireName(queue));
                break;

            case DispatchResult.IntentionallyUnconsumed:
                logger.LogInformation(
                    "message_type {MessageType} on queue {Queue} is declared intentionally unconsumed; " +
                    "treating as delivered. message_id={MessageId}",
                    envelope.MessageType,
                    LogicalQueueRegistry.WireName(queue),
                    envelope.MessageId);
                break;

            case DispatchResult.NoConsumer:
                // NOT delivered: the emitting handler expects a consumer that was never registered.
                // Throwing lets the outbox publisher dead-letter the row (visible + replayable after
                // the registration is fixed) instead of vanishing the message. The dedicated type
                // keeps consumer-thrown PoisonMessageExceptions on their own retry/DLQ path.
                throw new UnconsumedMessageTypeException(
                    $"No consumer registered for routable message_type '{envelope.MessageType}' on queue " +
                    $"'{LogicalQueueRegistry.WireName(queue)}' and it is not declared intentionally unconsumed " +
                    "(ConsumerRegistry.RegisterUnconsumed).");
        }
    }
}
