using Cordia.Domain.Exceptions;

namespace Cordia.Domain.TenantResolution;

/// <summary>
/// Signs and verifies a web-origin operator session as a compact HS256 JWS
/// (<c>base64url(header).base64url(payload).base64url(signature)</c>).
///
/// Distinct from <see cref="ITenantContextSigner"/>: header <c>typ</c> is <c>opctx+jws</c>
/// and the claim set is <see cref="OperatorSessionClaims"/> (no channel/policy fields —
/// langgraph <c>TenantContext</c> stays <c>extra=forbid</c>). Signing keys are the same
/// custody seam as tenant context (<c>ISecretStore</c> <c>TenantContextSigningKeys</c>).
///
/// Implementations must never log or serialize the signing key material, and any failure
/// message must never contain the secret, key id, or claim values.
/// </summary>
public interface IOperatorSessionSigner
{
    /// <summary>Signs <paramref name="claims"/> and returns a compact HS256 JWS.</summary>
    string Sign(OperatorSessionClaims claims);

    /// <summary>
    /// Verifies <paramref name="jws"/> and returns the decoded claims.
    /// Throws <see cref="TenantContextVerificationException"/> on any failure (unknown/retired
    /// <c>kid</c>, bad signature, expired, future <c>issued_at</c> beyond skew, wrong <c>typ</c>,
    /// or a malformed token).
    /// </summary>
    OperatorSessionClaims Verify(string jws);
}

/// <summary>
/// Web-origin operator-session claim set. Decoupled from <see cref="TenantContextClaims"/> so
/// the operator JWS cannot be confused with a channel TenantContext (Inv-13).
/// </summary>
public sealed record OperatorSessionClaims(
    string TenantId,
    string ActorId,
    string ActorRole,
    string SessionId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
