using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using MediatR;

namespace Cordia.Application.Messaging;

/// <summary>
/// MediatR-backed implementation of <see cref="IQueueConsumerDispatcher"/> (§3, OD-1).
/// Looks up the registered consumer request factory from <see cref="ConsumerRegistry"/> and sends
/// it via <see cref="IMediator"/>. Inbox deduplication (T4) will wrap the Send automatically via
/// the MediatR pipeline behavior registered in spec 05 T4.
/// </summary>
public sealed class MediatRQueueConsumerDispatcher(
    IMediator mediator,
    ConsumerRegistry registry) : IQueueConsumerDispatcher
{
    public async Task<DispatchResult> DispatchAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
    {
        if (!registry.TryGet(envelope.MessageType, out var factory))
        {
            return registry.IsIntentionallyUnconsumed(envelope.MessageType)
                ? DispatchResult.IntentionallyUnconsumed
                : DispatchResult.NoConsumer;
        }

        var request = factory(envelope);
        await mediator.Send(request, ct);
        return DispatchResult.Dispatched;
    }
}
