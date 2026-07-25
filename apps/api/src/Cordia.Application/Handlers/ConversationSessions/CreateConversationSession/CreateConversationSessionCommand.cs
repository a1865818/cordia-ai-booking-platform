using Cordia.Domain.Enums;
using MediatR;

namespace Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;

/// <summary>
/// The spec-15 D12 single-writer command: the channel gateway causes the Booking API to insert the
/// tenant-scoped <c>conversation_sessions</c> row at session start. Consent_* columns are left null
/// for the spec-14 foreground consent gate to UPDATE later. This is the ONLY insert path for the
/// table (the consent/transcript handlers only read/update).
///
/// Per spec-15 §5.4 the row also carries <c>channel_endpoint_id</c> + <c>correlation_id</c> so it
/// links back to the resolving endpoint and the ingress correlation chain; both are persisted on the
/// row (nullable columns) as well as riding the signed <c>TenantContext</c> claim and the
/// tenant.resolution audit row.
/// </summary>
public sealed record CreateConversationSessionCommand(
    string TenantId,
    string SessionId,
    TenantChannel Channel,
    string ChannelEndpointId,
    string CorrelationId) : IRequest<ConversationSessionDto>;

/// <summary>
/// Result of a session create — the created (or idempotently pre-existing) session identity.
/// <see cref="ChannelEndpointId"/>/<see cref="CorrelationId"/> are the persisted row's endpoint +
/// correlation linkage (spec-15 §5.4).
/// </summary>
public sealed record ConversationSessionDto(
    string TenantId,
    string SessionId,
    TenantChannel Channel,
    string ChannelEndpointId,
    string CorrelationId,
    DateTimeOffset StartedAt,
    long Version);
