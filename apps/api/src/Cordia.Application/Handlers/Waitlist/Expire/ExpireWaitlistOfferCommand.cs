using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Expire;

/// <summary>
/// Queue-driven command realizing <c>waitlist.commands.offer_expiry.v1</c> (spec 11 §5.1): the
/// deadline on an outstanding offer has elapsed, so the offer is expired, its temporary hold is
/// released, and the freed opening is re-matched to the next eligible enrollee.
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so <c>InboxDeduplicationBehavior</c> wraps
/// all writes in ONE transaction (consumer <c>"waitlist.processor"</c>, Inv-5). The handler MUST
/// NOT open its own transaction. Settles (no-op) without throwing when the offer is no longer
/// Offered (already accepted/declined/expired) so a late timer fire is harmless.</para>
/// </summary>
public sealed record ExpireWaitlistOfferCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string OfferId) : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "waitlist.processor";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => null;
}
