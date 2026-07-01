using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.BookingSignal;

/// <summary>
/// Queue-driven 06→saga signal realizing <c>preorder.commands.booking_failed.v1</c> (spec 11 §5.3):
/// the spec-06 engine reached a terminal failure for a pre-order operation (06 already ran its own
/// ambiguous→reconcile guard, Inv-7). Advances the saga <c>CommittingBooking → CompensatingItems</c>
/// so the reserved items are released and one combined failure outcome is emitted. Inbox-deduplicated
/// (consumer <c>"preorder.processor"</c>); MUST NOT open its own transaction. Resolves the saga by
/// operation id.
/// </summary>
public sealed record PreorderBookingFailedCommand(
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
