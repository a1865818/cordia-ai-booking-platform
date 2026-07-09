using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Match;

/// <summary>
/// Queue-driven command realizing <c>waitlist.commands.match.v1</c> (spec 11 §5.1): an opening
/// (a freed first-party slot) is offered to the next eligible enrollee. Carries the matched-slot
/// criteria so the handler can find an enrollee, reserve a temporary hold, and make the offer.
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so <c>InboxDeduplicationBehavior</c> wraps
/// all writes in ONE transaction (consumer <c>"waitlist.processor"</c>, Inv-5). The handler MUST
/// NOT open its own transaction.</para>
/// </summary>
public sealed record MatchWaitlistOfferCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string LocationId,
    string ServiceId,
    string SlotId,
    DateTimeOffset SlotStartAt,
    int AvailableSeats) : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "waitlist.processor";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;

    /// <summary>Matching mints its own provisional operation id per offer; not tied to an inbound operation.</summary>
    string? IInboxConsumerRequest.OperationId => null;
}
