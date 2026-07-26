using System.Text.Json.Serialization;
using Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;
using Cordia.Domain.Enums;
using Cordia.Domain.TenantResolution;

namespace Cordia.Api.Controllers.Channels;

/// <summary>
/// The channel-gateway ingress response (spec-15 §7): the minted compact HS256 JWS plus the decoded
/// claim set it carries. Returned identically by <see cref="TelephonyWebhookController"/> and
/// <see cref="MessagingWebhookController"/>, so the shape lives in one file (both controllers' own
/// request DTOs stay co-located with the controller).
///
/// <para>The JWS is only integrity-protected (HS256), never encrypted — the caller already receives
/// the claims base64-encoded inside <c>tenant_context</c>. Echoing the decoded <c>claims</c> here is
/// therefore not an additional disclosure to the token holder (it saves the voice pipeline / agent a
/// decode). NO signing key or secret is ever placed in the body (Inv-12).</para>
/// </summary>
public sealed record TenantContextResponse(
    [property: JsonPropertyName("tenant_context")] string TenantContext,
    [property: JsonPropertyName("claims")] TenantContextClaimsResponse Claims)
{
    /// <summary>Projects the Application result onto the wire contract.</summary>
    public static TenantContextResponse From(SignedTenantContext signed)
        => new(signed.Jws, TenantContextClaimsResponse.From(signed.Claims));
}

/// <summary>
/// Snake_case wire projection of <see cref="TenantContextClaims"/> (the §5.1 claim set). Explicit
/// <c>[JsonPropertyName]</c> attributes freeze the wire names independent of serializer defaults.
/// </summary>
public sealed record TenantContextClaimsResponse(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("channel")] TenantChannel Channel,
    [property: JsonPropertyName("channel_endpoint_id")] string ChannelEndpointId,
    [property: JsonPropertyName("configuration_version")] string ConfigurationVersion,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("allowed_agent_policy_ids")] IReadOnlyList<string> AllowedAgentPolicyIds,
    [property: JsonPropertyName("allowed_tool_policy_ids")] IReadOnlyList<string> AllowedToolPolicyIds,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Maps the Domain claim record onto the wire projection.</summary>
    public static TenantContextClaimsResponse From(TenantContextClaims c)
        => new(
            c.TenantId,
            c.Channel,
            c.ChannelEndpointId,
            c.ConfigurationVersion,
            c.CorrelationId,
            c.SessionId,
            c.AllowedAgentPolicyIds,
            c.AllowedToolPolicyIds,
            c.IssuedAt,
            c.ExpiresAt);
}
