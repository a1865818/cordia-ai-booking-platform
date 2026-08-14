using MediatR;

namespace Cordia.Application.Handlers.Auth.Logout;

/// <summary>
/// Revokes the operator session carried by a verified <c>opctx+jws</c>.
/// Idempotent: already-revoked or missing rows are a no-op (HTTP 204).
/// </summary>
public sealed record LogoutCommand(string TenantId, string Jws) : IRequest<Unit>;
