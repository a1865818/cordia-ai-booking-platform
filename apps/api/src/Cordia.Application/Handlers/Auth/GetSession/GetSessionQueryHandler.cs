using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using MediatR;

namespace Cordia.Application.Handlers.Auth.GetSession;

/// <summary>
/// Confirms the operator session is still live (<c>revoked_at</c> null and not expired)
/// and returns actor/tenant/expiry. Middleware already verified the JWS and checked
/// revocation for non-logout paths; this is the explicit session-read check.
/// </summary>
public sealed class GetSessionQueryHandler(
    IOperatorSessionSigner signer,
    IOperatorSessionRepository sessions,
    TimeProvider timeProvider)
    : IRequestHandler<GetSessionQuery, SessionResult>
{
    public async Task<SessionResult> Handle(GetSessionQuery request, CancellationToken ct)
    {
        var claims = signer.Verify(request.Jws);

        var session = await sessions.GetAsync(request.TenantId, claims.SessionId, ct);
        var now = timeProvider.GetUtcNow();
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
            throw new TenantContextVerificationException("Operator session is not active.");

        return new SessionResult(
            claims.ActorId,
            claims.ActorRole,
            claims.TenantId,
            session.ExpiresAt);
    }
}
