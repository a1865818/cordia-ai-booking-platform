using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.CommitPreorder;

/// <summary>
/// Queue-driven step realizing <c>preorder.commands.commit_preorder.v1</c> (spec 11 §5.3): commits
/// the pre-order, completing the saga, and emits the single combined-outcome notification (Inv-8).
/// Inbox-deduplicated (consumer <c>"preorder.processor"</c>); MUST NOT open its own transaction.
/// </summary>
public sealed record CommitPreorderCommand(
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
