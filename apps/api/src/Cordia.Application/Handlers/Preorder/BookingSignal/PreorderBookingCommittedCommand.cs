using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.BookingSignal;

/// <summary>
/// Queue-driven 06→saga signal realizing <c>preorder.commands.booking_committed.v1</c> (spec 11 §5.3):
/// the spec-06 engine has durably committed the booking for a pre-order operation. Advances the saga
/// <c>CommittingBooking → CommittingPreorder</c> and enqueues the final commit step. Inbox-deduplicated
/// (consumer <c>"preorder.processor"</c>); MUST NOT open its own transaction. Carries no saga id — the
/// handler resolves the saga by operation id.
/// </summary>
public sealed record PreorderBookingCommittedCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string OperationId) : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => PreorderSagaMessages.ConsumerName;
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
