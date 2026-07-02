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

    private readonly Dictionary<string, string> _intentionallyUnconsumed =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a factory for <paramref name="messageType"/>.
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="messageType"/> is already registered.
    /// </summary>
    public void Register(string messageType, Func<JobEnvelope, IBaseRequest> factory)
    {
        if (_intentionallyUnconsumed.ContainsKey(messageType))
            throw new InvalidOperationException(
                $"message_type '{messageType}' is declared intentionally unconsumed; remove that declaration to register a consumer.");
        if (!_factories.TryAdd(messageType, factory))
            throw new InvalidOperationException(
                $"A consumer factory for message_type '{messageType}' is already registered.");
    }

    /// <summary>
    /// Declares that <paramref name="messageType"/> is deliberately emitted WITHOUT a consumer
    /// (audit-trail / future-consumer event). The transport acks these as delivered; any other
    /// routable type with no consumer is dead-lettered as a registration defect — the silent-drop
    /// behaviour that hid the spec 11 waitlist-offer / preorder-alternatives notifications.
    /// </summary>
    public void RegisterUnconsumed(string messageType, string reason)
    {
        if (_factories.ContainsKey(messageType))
            throw new InvalidOperationException(
                $"message_type '{messageType}' already has a consumer; it cannot also be declared unconsumed.");
        if (!_intentionallyUnconsumed.TryAdd(messageType, reason))
            throw new InvalidOperationException(
                $"message_type '{messageType}' is already declared intentionally unconsumed.");
    }

    /// <summary>True when <paramref name="messageType"/> is declared deliberately consumer-less.</summary>
    public bool IsIntentionallyUnconsumed(string messageType) =>
        _intentionallyUnconsumed.ContainsKey(messageType);

    /// <summary>
    /// Attempts to retrieve the factory registered for <paramref name="messageType"/>.
    /// Returns <see langword="true"/> and sets <paramref name="factory"/> on success.
    /// </summary>
    public bool TryGet(string messageType, out Func<JobEnvelope, IBaseRequest> factory) =>
        _factories.TryGetValue(messageType, out factory!);
}
