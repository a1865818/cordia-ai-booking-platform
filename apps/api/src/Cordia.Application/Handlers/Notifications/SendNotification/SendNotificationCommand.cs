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
///
/// <para><see cref="RequiresOperation"/> is <see langword="false"/> for events whose
/// <see cref="OperationId"/> has no <c>booking_operations</c> row yet — e.g. a spec-11 waitlist
/// offer, whose <c>op_wl_*</c> operation is only minted when the offer is accepted. Those send
/// with material revision 0 instead of ack-and-dropping on the missing row.</para>
/// </summary>
public sealed record SendNotificationCommand(
    string TenantId,
    string OperationId,
    string MessageId,
    NotificationEvent EventType,
    MessagePriority Priority,
    string? CorrelationId,
    bool RequiresOperation = true)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "notification.sender";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
