using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.UserAction;

/// <summary>
/// Queue-driven command for the "user action completed" event (spec 06 §6.1).
///
/// AwaitingUserAction → Accepted + emit booking.command.requested.v1 continuation outbox
/// (re-drive validation) + audit operation.user_action_completed.
///
/// Implements <see cref="IInboxConsumerRequest"/> so InboxDeduplicationBehavior wraps all
/// writes in one transaction. The handler MUST NOT open its own transaction.
/// </summary>
public sealed record ResumeFromUserActionCommand(
    string TenantId,
    string OperationId,
    string MessageId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "booking.user_action";
    string? IInboxConsumerRequest.CorrelationId => OperationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
