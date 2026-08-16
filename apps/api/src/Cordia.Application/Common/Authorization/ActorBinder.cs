using Cordia.Domain.Interfaces;

namespace Cordia.Application.Common.Authorization;

/// <summary>
/// Resolves the console actor for a request. A verified operator session
/// (<see cref="IActorContext.ActorId"/> non-empty, set by
/// <c>TenantContextVerificationMiddleware</c> from an <c>opctx+jws</c>) WINS and
/// ignores the <c>X-Actor</c> / <c>X-Actor-Role</c> shims. Otherwise the headers
/// are returned as-is (dev/test shim).
/// </summary>
public static class ActorBinder
{
    public static (string Actor, string Role) Resolve(
        IActorContext ctx, string? headerActor, string? headerRole)
    {
        if (!string.IsNullOrEmpty(ctx.ActorId))
            return (ctx.ActorId, ctx.ActorRole ?? string.Empty);

        return (headerActor ?? string.Empty, headerRole ?? string.Empty);
    }
}
