namespace Cordia.Domain.Interfaces;

/// <summary>The authenticated console actor for the current request, if any.</summary>
public interface IActorContext
{
    string? ActorId { get; }

    string? ActorRole { get; }
}
