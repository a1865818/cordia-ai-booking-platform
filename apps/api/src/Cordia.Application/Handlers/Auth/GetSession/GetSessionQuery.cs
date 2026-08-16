using System.Text.Json.Serialization;
using MediatR;

namespace Cordia.Application.Handlers.Auth.GetSession;

/// <summary>
/// Returns the live operator session for a verified <c>opctx+jws</c>.
/// Requires a verified operator context (JWS in <see cref="Jws"/>); a channel
/// <c>tenantctx+jws</c> fails verification here.
/// </summary>
public sealed record GetSessionQuery(string TenantId, string Jws) : IRequest<SessionResult>;

/// <summary>Wire shape for GET /api/auth/session (snake_case).</summary>
public sealed record SessionResult(
    [property: JsonPropertyName("actor_id")] string ActorId,
    [property: JsonPropertyName("actor_role")] string ActorRole,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
