using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Services;

/// <summary>Scoped, settable implementation of <see cref="IActorContext"/>.</summary>
public sealed class ActorContext : IActorContext
{
    public string? ActorId { get; set; }

    public string? ActorRole { get; set; }
}
