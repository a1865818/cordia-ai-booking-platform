using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.ReserveItems;

/// <summary>
/// Queue-driven step realizing <c>preorder.commands.reserve_items.v1</c> (spec 11 §5.3): reserves the
/// pre-order's catalog items against the (stub-ported) inventory boundary. Inbox-deduplicated
/// (consumer <c>"preorder.processor"</c>); MUST NOT open its own transaction.
/// </summary>
public sealed record ReservePreorderItemsCommand(
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
