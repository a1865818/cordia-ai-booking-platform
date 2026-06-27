using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
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
    public async Task PublishAsync_WhenDispatcherReturnsTrue_CompletesWithoutException()
    {
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await transport.PublishAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None);

        dispatcherMock.Verify(
            d => d.DispatchAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenDispatcherReturnsFalse_CompletesWithoutException()
    {
        var dispatcherMock = new Mock<IQueueConsumerDispatcher>();
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var transport = new PostgresMessageTransport(
            dispatcherMock.Object,
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await transport.PublishAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None);

        dispatcherMock.Verify(
            d => d.DispatchAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None),
            Times.Once);
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
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.PublishAsync(envelope, LogicalQueue.BookingCommands, CancellationToken.None));
    }
}
