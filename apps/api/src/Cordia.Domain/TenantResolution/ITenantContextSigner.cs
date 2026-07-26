using Cordia.Domain.Enums;

namespace Cordia.Domain.TenantResolution;

/// <summary>
/// Signs and verifies the spec-15 §5.1 <c>TenantContext</c> claim set as a compact
/// HS256 JWS (<c>base64url(header).base64url(payload).base64url(signature)</c>).
///
/// This is the trust boundary at channel ingress (Inv-13): downstream verifiers accept
/// a <c>tenant_id</c> only from a context that verifies here. The wire form is frozen and
/// shared byte-for-byte with the <c>apps/langgraph</c> verifier — see
/// <c>apps/langgraph/tests/_stubs/tenant_context_stub.py</c>.
///
/// Implementations must never log or serialize the signing key material, and any failure
/// message must never contain the secret, key id, or claim values.
/// </summary>
public interface ITenantContextSigner
{
    /// <summary>Signs <paramref name="claims"/> and returns a compact HS256 JWS.</summary>
    string Sign(TenantContextClaims claims);

    /// <summary>
    /// Verifies <paramref name="jws"/> and returns the decoded claims.
    /// Throws <see cref="Cordia.Domain.Exceptions.TenantContextVerificationException"/> on any
    /// failure (unknown/retired <c>kid</c>, bad signature, expired, future <c>issued_at</c> beyond
    /// skew, wrong <c>typ</c>, or a malformed token).
    /// </summary>
    TenantContextClaims Verify(string jws);
}

/// <summary>
/// The spec-15 §5.1 TenantContext claim set (hand-written Domain record, decoupled from the
/// quicktype-generated <c>Cordia.Domain.Generated.TenantContext</c> so the signer fully controls
/// canonical serialization). <see cref="Channel"/> uses the hand-written
/// <see cref="TenantChannel"/> enum; timestamps are <see cref="DateTimeOffset"/>.
/// </summary>
public sealed record TenantContextClaims(
    string TenantId,
    TenantChannel Channel,
    string ChannelEndpointId,
    string ConfigurationVersion,
    string CorrelationId,
    string SessionId,
    IReadOnlyList<string> AllowedAgentPolicyIds,
    IReadOnlyList<string> AllowedToolPolicyIds,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
