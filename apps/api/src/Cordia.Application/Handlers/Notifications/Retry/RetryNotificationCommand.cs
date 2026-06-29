using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Notifications.Retry;

/// <summary>
/// Queue-driven command that re-executes delivery for a <c>FailedTransient</c> notification
/// (spec 10 FM#6, T5a). Fired by <c>ScheduledJobPollerHostedService</c> after
/// <c>next_eligible_at</c> has passed.
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so
/// <c>InboxDeduplicationBehavior</c> wraps all writes in one ambient transaction.
/// The handler MUST NOT open its own transaction.</para>
/// </summary>
public sealed record RetryNotificationCommand(
    string TenantId,
    string NotificationId,
    string MessageId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "notification.sender";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => null;
    string? IInboxConsumerRequest.OperationId => null;
}
