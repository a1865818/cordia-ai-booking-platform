using Cordia.Application.Messaging;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Behaviours;

/// <summary>
/// MediatR pipeline behaviour that provides inbox deduplication for queue-driven consumer
/// requests (§3, Inv-5, §5.1).
///
/// <para><b>Passthrough for non-queue requests.</b>  If the incoming <typeparamref name="TRequest"/>
/// does not implement <see cref="IInboxConsumerRequest"/> the behaviour is a no-op passthrough —
/// exactly as <c>ValidationPipelineBehavior</c> skips requests without validators.
/// This keeps all existing HTTP-driven commands unaffected.</para>
///
/// <para><b>Admit-once (happy path).</b>  For queue-driven requests the behaviour opens ONE
/// transaction (via <see cref="IUnitOfWork"/>) and atomically inserts a <c>consumer_inbox</c>
/// row with <c>outcome = admitted</c> before calling the handler.  The handler's durable writes
/// (e.g. creating/advancing a <c>workflow_step</c>) participate in the SAME transaction and commit
/// together (Inv-5, §5.1).  <c>Outcome = admitted</c> means "durable step created, external effect
/// NOT yet done"; the step sweeper / spec-06 worker drives admitted → completed (§12.4, T6/spec 06).
/// Consumer handlers MUST NOT open their own transactions — they share the ambient scoped
/// <c>DbContext</c>.</para>
///
/// <para><b>Duplicate suppression (Inv-5, §5.2).</b>  A duplicate delivery of the same
/// <c>(consumer_name, tenant_id, message_id)</c> hits the unique constraint;
/// <see cref="IInboxRepository.TryAdmitAsync"/> returns <see langword="false"/> (DO NOTHING path).
/// The handler is NOT invoked, no second durable step is created, and the call returns
/// <see langword="default"/> so the broker-level caller can ack-and-drop without throwing.</para>
///
/// <para><b>Handler failure → rollback, no partial state (§8).</b>  An exception thrown by the
/// handler propagates out of <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>, which rolls back
/// the transaction.  No <c>consumer_inbox</c> row and no partial state survive; the broker
/// redelivers the message cleanly.</para>
/// </summary>
public sealed class InboxDeduplicationBehavior<TRequest, TResponse>(
    IUnitOfWork uow,
    IInboxRepository inboxRepo,
    Metrics metrics)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // No-op passthrough for all non-queue-driven requests (mirrors ValidationPipelineBehavior).
        if (request is not IInboxConsumerRequest inbox)
            return await next(cancellationToken);

        // Stays default! on the duplicate path (handler never runs) → ack-and-drop.
        TResponse response = default!;

        await uow.ExecuteInTransactionAsync(async ct =>
        {
            var admitted = await inboxRepo.TryAdmitAsync(new ConsumerInbox
            {
                TenantId = inbox.TenantId,
                ConsumerName = inbox.ConsumerName,
                MessageId = inbox.MessageId,
                CorrelationId = inbox.CorrelationId,
                OperationId = inbox.OperationId,
                Outcome = InboxOutcome.Admitted,
                AdmittedAt = DateTimeOffset.UtcNow,
            }, ct);

            if (!admitted)
            {
                metrics.FulfillmentInboxDedupSuppressedTotal.Add(1);
                metrics.FulfillmentDeliveryDuplicateTotal.Add(1);
                return; // duplicate: nothing else written; txn commits as a no-op
            }

            response = await next(ct); // handler creates/advances the durable step in THIS txn
        }, cancellationToken);

        return response;
    }
}
