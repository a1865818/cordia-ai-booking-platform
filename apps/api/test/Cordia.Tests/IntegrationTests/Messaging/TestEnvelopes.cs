using System.Text.Json;
using Cordia.Domain.Messaging;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Shared envelope builder for messaging integration tests (T3–T7).
/// </summary>
internal static class TestEnvelopes
{
    /// <summary>Builds a <see cref="JobEnvelope"/> with the given fields and a parsed payload.</summary>
    internal static JobEnvelope Build(
        string tenantId,
        string messageId,
        string messageType,
        string payloadJson)
    {
        JsonElement payloadElement;
        try
        {
            payloadElement = JsonDocument.Parse(payloadJson).RootElement.Clone();
        }
        catch
        {
            payloadElement = JsonDocument.Parse("{}").RootElement.Clone();
        }

        return new JobEnvelope(
            MessageId: messageId,
            MessageType: messageType,
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: tenantId,
            CorrelationId: messageId,
            CausationId: null,
            OperationId: null,
            IntentId: null,
            IntentRevision: null,
            AttemptHint: 0,
            Payload: payloadElement);
    }
}
