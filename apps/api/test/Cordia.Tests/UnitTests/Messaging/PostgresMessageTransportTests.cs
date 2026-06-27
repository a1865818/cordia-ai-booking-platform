using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Tenancy;
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
            new TenantContext(),
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
            new TenantContext(),
            NullLogger<PostgresMessageTransport>.Instance);

        var envelope = BuildEnvelope();
        await transport.PublishAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None);

        dispatcherMock.Verify(
            d => d.DispatchAsync(envelope, LogicalQueue.NotificationCommands, CancellationToken.None),
            Times.Once);
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
            .ReturnsAsync(true);

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
