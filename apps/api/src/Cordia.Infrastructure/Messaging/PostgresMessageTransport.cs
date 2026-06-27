using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Services;
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
