using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.DepositEvent;

/// <summary>Indicates whether the deposit was paid or expired.</summary>
public enum DepositEventKind
{
    Paid,
    Expired,
}

/// <summary>
/// Queue-driven command for deposit lifecycle events (spec 06 §10).
///
/// <list type="bullet">
///   <item><c>Paid</c>: AwaitingPayment → Finalizing + DepositSession → Paid + emit
///     booking.command.requested.v1 continuation outbox + audit operation.deposit_paid.</item>
///   <item><c>Expired</c>: AwaitingPayment → Expired + DepositSession → Expired +
///     emit capacity.hold_release.requested.v1 + notification.deposit_expired.v1 + audit
///     operation.expired.</item>
/// </list>
///
/// Implements <see cref="IInboxConsumerRequest"/> so <c>InboxDeduplicationBehavior</c> wraps
/// all writes in one transaction (admit + handler writes share the ambient scoped DbContext).
/// The handler MUST NOT open its own transaction.
/// </summary>
public sealed record ProcessDepositEventCommand(
    string TenantId,
    string OperationId,
    DepositEventKind Kind,
    string MessageId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "booking.deposit";
    string? IInboxConsumerRequest.CorrelationId => OperationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
