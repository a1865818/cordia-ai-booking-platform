using Cordia.Application.Messaging;
using Cordia.Domain.Enums;
using MediatR;

namespace Cordia.Application.Handlers.Notifications.SendNotification;

/// <summary>
/// Queue-driven command for sending a customer- or owner-facing notification (spec 10 §4).
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so <c>InboxDeduplicationBehavior</c>
/// wraps all writes in one transaction (admit + handler writes share the ambient scoped
/// DbContext). The handler MUST NOT open its own transaction.</para>
///
/// <para>The concrete message-type factories in <c>ApplicationServiceCollectionExtensions</c>
/// are the realization of the spec §5 <c>notification.requested.v1</c> logical contract.</para>
/// </summary>
public sealed record SendNotificationCommand(
    string TenantId,
    string OperationId,
    string MessageId,
    NotificationEvent EventType,
    MessagePriority Priority,
    string? CorrelationId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "notification.sender";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
