using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.CompensateBooking;

/// <summary>
/// Queue-driven compensation realizing <c>preorder.commands.compensate_booking.v1</c> (spec 11 §5.3):
/// reached when booking commit fails after the deposit was authorized. Voids the deposit and releases
/// the items; terminates FailedFinal when the void is confirmed, or ManualReview when it cannot be
/// confirmed (Inv-7). Inbox-deduplicated (consumer <c>"preorder.processor"</c>); MUST NOT open its
/// own transaction.
/// </summary>
public sealed record CompensatePreorderBookingCommand(
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
