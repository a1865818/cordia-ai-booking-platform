namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a signed <c>TenantContext</c> JWS fails verification (spec-15, Inv-13):
/// unknown or retired <c>kid</c>, bad signature, expired <c>expires_at</c>, future
/// <c>issued_at</c> beyond the allowed skew, wrong <c>typ</c>, or a malformed token.
///
/// The message is a fixed, generic string — it must NEVER contain the signing secret,
/// the key id, or any claim value (Inv-12). Mapped by the GlobalExceptionHandler to a
/// 401/403 (spec-15 Phase 4).
/// </summary>
public sealed class TenantContextVerificationException(string message)
    : DomainException(message);
