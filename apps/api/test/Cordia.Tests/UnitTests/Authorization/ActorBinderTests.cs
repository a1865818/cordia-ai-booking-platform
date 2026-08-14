using Cordia.Application.Common.Authorization;
using Cordia.Domain.Interfaces;

namespace Cordia.Tests.UnitTests.Authorization;

/// <summary>
/// Verified operator context (non-empty <c>ActorId</c>) wins over the X-Actor / X-Actor-Role
/// shims; otherwise the headers are returned as-is.
/// </summary>
[Trait("Category", "Unit")]
public class ActorBinderTests
{
    [Fact]
    public void Resolve_VerifiedContext_WinsAndIgnoresHeaders()
    {
        var ctx = new StubActor("opr-verified", "owner");

        var (actor, role) = ActorBinder.Resolve(ctx, headerActor: "shim-actor", headerRole: "operator");

        Assert.Equal("opr-verified", actor);
        Assert.Equal("owner", role);
    }

    [Fact]
    public void Resolve_EmptyContext_ReturnsHeaders()
    {
        var ctx = new StubActor(null, null);

        var (actor, role) = ActorBinder.Resolve(ctx, headerActor: "shim-actor", headerRole: "operator");

        Assert.Equal("shim-actor", actor);
        Assert.Equal("operator", role);
    }

    [Fact]
    public void Resolve_EmptyContextAndMissingHeaders_ReturnsEmptyStrings()
    {
        var ctx = new StubActor(string.Empty, string.Empty);

        var (actor, role) = ActorBinder.Resolve(ctx, headerActor: null, headerRole: null);

        Assert.Equal(string.Empty, actor);
        Assert.Equal(string.Empty, role);
    }

    private sealed class StubActor(string? id, string? role) : IActorContext
    {
        public string? ActorId { get; } = id;

        public string? ActorRole { get; } = role;
    }
}
