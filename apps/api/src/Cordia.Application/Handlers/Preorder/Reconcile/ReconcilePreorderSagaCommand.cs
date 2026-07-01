using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.Reconcile;

/// <summary>
/// Queue-driven recovery realizing <c>preorder.commands.reconcile.v1</c> (spec 11 §5.3): fired by a
/// step timeout or the crash-recovery sweep for a saga that has not progressed. Re-enqueues the step
/// matching the persisted <c>SagaState</c> (idempotent re-entry), or escalates when no acquiring step
/// has run (Revalidating → FailedFinal) or the saga is already parked (Reconciling → ManualReview).
/// Inbox-deduplicated (consumer <c>"preorder.processor"</c>); MUST NOT open its own transaction.
/// </summary>
public sealed record ReconcilePreorderSagaCommand(
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
