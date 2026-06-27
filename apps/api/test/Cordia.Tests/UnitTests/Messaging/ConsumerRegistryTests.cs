using Cordia.Application.Messaging;
using Cordia.Domain.Messaging;
using MediatR;
using System.Text.Json;

namespace Cordia.Tests.UnitTests.Messaging;

[Trait("Category", "Unit")]
public class ConsumerRegistryTests
{
    private static JobEnvelope BuildEnvelope() =>
        new(
            MessageId: "msg_registry_001",
            MessageType: "booking.finalize.requested.v1",
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
    public void Register_ThenTryGet_ReturnsTrueAndFactory()
    {
        var registry = new ConsumerRegistry();
        Func<JobEnvelope, IBaseRequest> factory = env => new TestConsumerRequest(env.MessageId);

        registry.Register("booking.finalize.requested.v1", factory);
        var found = registry.TryGet("booking.finalize.requested.v1", out var retrieved);

        Assert.True(found);
        Assert.NotNull(retrieved);
        var envelope = BuildEnvelope();
        var request = retrieved!(envelope);
        Assert.IsType<TestConsumerRequest>(request);
        Assert.Equal(envelope.MessageId, ((TestConsumerRequest)request).MessageId);
    }

    [Fact]
    public void TryGet_UnknownMessageType_ReturnsFalse()
    {
        var registry = new ConsumerRegistry();

        var found = registry.TryGet("unknown.type.v1", out var factory);

        Assert.False(found);
        Assert.Null(factory);
    }

    [Fact]
    public void Register_DuplicateMessageType_Throws()
    {
        var registry = new ConsumerRegistry();
        Func<JobEnvelope, IBaseRequest> factory = env => new TestConsumerRequest(env.MessageId);

        registry.Register("booking.finalize.requested.v1", factory);

        Assert.Throws<InvalidOperationException>(
            () => registry.Register("booking.finalize.requested.v1", factory));
    }
}
