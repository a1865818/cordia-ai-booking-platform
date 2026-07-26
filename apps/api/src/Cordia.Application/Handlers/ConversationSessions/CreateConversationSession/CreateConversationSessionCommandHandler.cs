using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;

/// <summary>
/// Inserts the tenant-scoped <c>conversation_sessions</c> row at channel ingress (spec-15 D12).
/// StartedAt is stamped from the injected clock, Version starts at 0, and all consent_* fields are
/// left null for the spec-14 consent gate to UPDATE later. Idempotent on <c>(tenant_id, session_id)</c>
/// — the repository swallows a unique-violation and returns the pre-existing row (reconnect/retry).
/// </summary>
public sealed class CreateConversationSessionCommandHandler(
    IConversationSessionRepository sessions,
    TimeProvider timeProvider)
    : IRequestHandler<CreateConversationSessionCommand, ConversationSessionDto>
{
    public async Task<ConversationSessionDto> Handle(CreateConversationSessionCommand command, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        // channel_endpoint_id / correlation_id are persisted on the row (spec-15 §5.4) so it links
        // back to the resolving endpoint and the ingress correlation chain. Consent_* left null for
        // the spec-14 consent gate to UPDATE later (D12).
        var persisted = await sessions.CreateAsync(
            new ConversationSession
            {
                TenantId = command.TenantId,
                SessionId = command.SessionId,
                Channel = command.Channel,
                ChannelEndpointId = command.ChannelEndpointId,
                CorrelationId = command.CorrelationId,
                StartedAt = now,
                Version = 0,
            },
            ct);

        // Echo the persisted values (on the idempotent reconnect path CreateAsync returns the
        // pre-existing row, so these come from the row that actually persisted, not the retry input).
        return new ConversationSessionDto(
            persisted.TenantId,
            persisted.SessionId,
            persisted.Channel,
            persisted.ChannelEndpointId ?? command.ChannelEndpointId,
            persisted.CorrelationId ?? command.CorrelationId,
            persisted.StartedAt,
            persisted.Version);
    }
}
