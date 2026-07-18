using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.CompensateItems;

/// <summary>
/// Queue-driven compensation realizing <c>preorder.commands.compensate_items.v1</c> (spec 11 §5.3):
/// releases the reserved items, then unwinds to release the hold (booking-attached) or terminates
/// (standalone). Inbox-deduplicated (consumer <c>"preorder.processor"</c>); MUST NOT open its own
/// transaction.
/// </summary>
public sealed record CompensatePreorderItemsCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string SagaId,
    string OperationId) : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => PreorderSagaMessages.ConsumerName;
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
