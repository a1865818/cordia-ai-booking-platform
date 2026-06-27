namespace Cordia.Application.Messaging;

/// <summary>
/// Marker interface for queue-driven MediatR requests that must pass through the inbox
/// deduplication pipeline (<see cref="InboxDeduplicationBehavior{TRequest,TResponse}"/>).
///
/// Spec-06 consumer requests implement this; the registry factory (T2,
/// <see cref="ConsumerRegistry"/>) populates these fields from the <c>JobEnvelope</c> when
/// it constructs each concrete request from a delivered envelope. The behavior uses these
/// fields to build the <c>consumer_inbox</c> row that enforces Inv-5 (effectively-once delivery).
/// </summary>
public interface IInboxConsumerRequest
{
    /// <summary>Named consumer, e.g. <c>"booking.finalizer"</c> (§3). Forms part of the
    /// dedup composite key <c>(consumer_name, tenant_id, message_id)</c>.</summary>
    string ConsumerName { get; }

    string TenantId { get; }

    /// <summary>Envelope <c>message_id</c> — the dedup key. Globally unique per message.</summary>
    string MessageId { get; }

    string? CorrelationId { get; }

    string? OperationId { get; }
}
