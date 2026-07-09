using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.StartSaga;

/// <summary>
/// Queue-driven command realizing <c>preorder.commands.start.v1</c> (spec 11 §5.3): a confirmed
/// intent carrying a <c>PreorderReference</c> starts the saga. Creates the saga in
/// <c>Revalidating</c>, re-checks live item availability/price (Inv-10), then advances to
/// <c>ReservingItems</c> (items-first for every pre-order — booking is delegated to the spec-06
/// engine, divergence #2), or resolves to FailedFinal with an AlternativesRequired outcome when an
/// item is stale.
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so all writes run in ONE inbox transaction
/// (consumer <c>"preorder.processor"</c>, Inv-5/Inv-9); the handler MUST NOT open its own
/// transaction. The <c>PreorderReferenceJson</c> is the immutable reference copied from the
/// confirmed revision (part of the canonical hash, Inv-1/Inv-2) by the enqueuer.</para>
/// </summary>
public sealed record StartPreorderSagaCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string OperationId,
    string PreorderReferenceJson) : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => PreorderSagaMessages.ConsumerName;
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
