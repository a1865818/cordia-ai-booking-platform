using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using MediatR;

namespace Cordia.Application.Handlers.Auth.Logout;

/// <summary>
/// Stamps <c>revoked_at</c> on the session named in the JWS. Middleware skips the
/// revocation DB check on this path so a second logout of a revoked-but-unexpired
/// token still reaches here and returns 204.
/// </summary>
public sealed class LogoutCommandHandler(
    IOperatorSessionSigner signer,
    IOperatorSessionRepository sessions)
    : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Unit> Handle(LogoutCommand request, CancellationToken ct)
    {
        var claims = signer.Verify(request.Jws);
        await sessions.RevokeAsync(request.TenantId, claims.SessionId, ct);
        return Unit.Value;
    }
}
