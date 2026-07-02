using Cordia.Application.Messaging;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using MediatR;
using Moq;
using System.Text.Json;

namespace Cordia.Tests.UnitTests.Messaging;

/// <summary>Test-only consumer request used to verify dispatch.</summary>
public record TestConsumerRequest(string MessageId) : IRequest;

[Trait("Category", "Unit")]
public class MediatRQueueConsumerDispatcherTests
{
    private static JobEnvelope BuildEnvelope(string messageType = "booking.finalize.requested.v1") =>
        new(
            MessageId: "msg_dispatch_001",
            MessageType: messageType,
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: "tenant_test",
            CorrelationId: "corr_test",
            CausationId: null,
            OperationId: null,
            IntentId: null,
            IntentRevision: null,
            AttemptHint: 0,
            Payload: JsonDocument.Parse("{}").RootElement);

    [Fact]
    public async Task DispatchAsync_WhenRegistryEmpty_ReturnsFalse_AndMediatorNotCalled()
    {
        var mediatorMock = new Mock<IMediator>();
        var registry = new ConsumerRegistry(); // empty

        var dispatcher = new MediatRQueueConsumerDispatcher(mediatorMock.Object, registry);
        var envelope = BuildEnvelope();

        var result = await dispatcher.DispatchAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        Assert.Equal(DispatchResult.NoConsumer, result);
        mediatorMock.Verify(m => m.Send(It.IsAny<IBaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WhenConsumerRegistered_ReturnsTrue_AndMediatorCalledOnce()
    {
        var mediatorMock = new Mock<IMediator>();
        mediatorMock
            .Setup(m => m.Send(It.IsAny<IBaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Unit.Value);

        var registry = new ConsumerRegistry();
        registry.Register("booking.finalize.requested.v1",
            env => new TestConsumerRequest(env.MessageId));

        var dispatcher = new MediatRQueueConsumerDispatcher(mediatorMock.Object, registry);
        var envelope = BuildEnvelope("booking.finalize.requested.v1");

        var result = await dispatcher.DispatchAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        Assert.Equal(DispatchResult.Dispatched, result);
        // MediatR 13: mediator.Send(IBaseRequest, ct) routes through ISender.Send(IBaseRequest, ct);
        // verify using IBaseRequest predicate with explicit cast to avoid expression-tree limitations.
        mediatorMock.Verify(
            m => m.Send(
                It.Is<IBaseRequest>(r => ((TestConsumerRequest)r).MessageId == envelope.MessageId),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenMessageTypeUnregistered_ReturnsFalse()
    {
        var mediatorMock = new Mock<IMediator>();
        var registry = new ConsumerRegistry();
        registry.Register("notification.send.v1",
            env => new TestConsumerRequest(env.MessageId));

        var dispatcher = new MediatRQueueConsumerDispatcher(mediatorMock.Object, registry);
        var envelope = BuildEnvelope("booking.finalize.requested.v1"); // different type

        var result = await dispatcher.DispatchAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        Assert.Equal(DispatchResult.NoConsumer, result);
        mediatorMock.Verify(m => m.Send(It.IsAny<IBaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_WhenDeclaredUnconsumed_ReturnsIntentionallyUnconsumed()
    {
        var mediatorMock = new Mock<IMediator>();
        var registry = new ConsumerRegistry();
        registry.RegisterUnconsumed("notification.waitlisted.v1", "informational; no catalogue entry");

        var dispatcher = new MediatRQueueConsumerDispatcher(mediatorMock.Object, registry);
        var envelope = BuildEnvelope("notification.waitlisted.v1");

        var result = await dispatcher.DispatchAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None);

        Assert.Equal(DispatchResult.IntentionallyUnconsumed, result);
        mediatorMock.Verify(m => m.Send(It.IsAny<IBaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
