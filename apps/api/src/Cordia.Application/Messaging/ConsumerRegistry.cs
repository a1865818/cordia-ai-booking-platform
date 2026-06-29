using Cordia.Domain.Messaging;
using MediatR;

namespace Cordia.Application.Messaging;

/// <summary>
/// Maps a message_type to the consumer request it dispatches to.
/// Empty in spec 05; spec 06 registers booking/notification/etc. consumers.
/// Registered as a singleton — safe because the factory dictionary is write-once at startup.
/// </summary>
public sealed class ConsumerRegistry
{
    private readonly Dictionary<string, Func<JobEnvelope, IBaseRequest>> _factories =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a factory for <paramref name="messageType"/>.
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="messageType"/> is already registered.
    /// </summary>
    public void Register(string messageType, Func<JobEnvelope, IBaseRequest> factory)
    {
        if (!_factories.TryAdd(messageType, factory))
            throw new InvalidOperationException(
                $"A consumer factory for message_type '{messageType}' is already registered.");
    }

    /// <summary>
    /// Attempts to retrieve the factory registered for <paramref name="messageType"/>.
    /// Returns <see langword="true"/> and sets <paramref name="factory"/> on success.
    /// </summary>
    public bool TryGet(string messageType, out Func<JobEnvelope, IBaseRequest> factory) =>
        _factories.TryGetValue(messageType, out factory!);
}
