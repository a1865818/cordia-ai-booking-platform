using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace Cordia.Tests.UnitTests.Messaging;

[Trait("Category", "Unit")]
public class PostgresMessageTransportTests
{
    private static JobEnvelope BuildEnvelope(string messageType = "booking.finalize.requested.v1") =>
        new(
            MessageId: "msg_test_001",
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
    public async Task PublishAsync_WhenDispatched_CompletesWithoutException()
    {
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResult.Dispatched);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            new TenantContext(),
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await transport.PublishAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        dispatcherMock.Verify(
            d => d.DispatchAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenIntentionallyUnconsumed_TreatedAsDelivered()
    {
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResult.IntentionallyUnconsumed);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            new TenantContext(),
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await transport.PublishAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None);

        dispatcherMock.Verify(
            d => d.DispatchAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenNoConsumer_ThrowsPoison_SoThePublisherDeadLetters()
    {
        // Review PR #65 root cause: a routable type with no registered consumer was silently
        // marked delivered, vanishing customer-facing notifications. It must now surface loudly.
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResult.NoConsumer);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            new TenantContext(),
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope("waitlist.offer.notified.v1");
        // The DEDICATED type: consumer-thrown PoisonMessageExceptions must keep their own
        // retry-then-dead-letter path and never be mislabelled "unconsumed_message_type".
        await Assert.ThrowsAsync<Cordia.Domain.Exceptions.UnconsumedMessageTypeException>(
            () => transport.PublishAsync(envelope, LogicalQueue.WaitlistCommands, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_SetsScopedTenantFromEnvelope_BeforeDispatch()
    {
        var tenantContext = new TenantContext { TenantId = "" }; // background scope: no tenant set
        string? tenantSeenByDispatcher = null;

        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            // Capture the ambient tenant AT dispatch time — proves it was set before dispatch.
            .Callback(() => tenantSeenByDispatcher = tenantContext.TenantId)
            .ReturnsAsync(DispatchResult.Dispatched);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            tenantContext,
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope() with { TenantId = "tenant_from_envelope" };
        await transport.PublishAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        Assert.Equal("tenant_from_envelope", tenantSeenByDispatcher);
        Assert.Equal("tenant_from_envelope", tenantContext.TenantId);
    }

    [Fact]
    public async Task PublishAsync_WhenDispatcherThrows_PropagatesException()
    {
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("consumer exploded"));

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            new TenantContext(),
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.PublishAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None));
    }
}
