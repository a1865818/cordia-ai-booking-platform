using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Quarantines a failed message into <c>dead_letter_messages</c> with <c>ReplayState=Quarantined</c>
/// (spec 05 §5.3, §8, §10, T7).
///
/// <para><strong>Inv-14 CONTRACT:</strong> the caller (spec-06 consumer) MUST have already persisted
/// the business state (recoverable/terminal status), the ops work-item, and user comms — in a
/// SEPARATE, already-committed transaction — BEFORE calling this. Dead-lettering is a
/// transport-level step that never replaces customer-visible business state (§6/§8/§17.2).</para>
/// </summary>
public sealed class DeadLetterHandler(
    IDeadLetterRepository dlqRepo,
    ILogger<DeadLetterHandler> logger)
{
    /// <summary>
    /// Inserts a quarantined dead-letter row for <paramref name="envelope"/>.
    /// <paramref name="attemptHistoryJson"/> should be a JSON array (defaults to <c>[]</c> if
    /// null or empty).
    /// </summary>
    public async Task QuarantineAsync(
        JobEnvelope envelope,
        string consumerName,
        string errorClass,
        string attemptHistoryJson,
        DateTimeOffset firstFailedAt,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var history = string.IsNullOrWhiteSpace(attemptHistoryJson) ? "[]" : attemptHistoryJson;

        var message = new DeadLetterMessage
        {
            TenantId = envelope.TenantId,
            MessageId = envelope.MessageId,
            ConsumerName = consumerName,
            MessageType = envelope.MessageType,
            Payload = envelope.Payload.GetRawText(),
            ErrorClass = errorClass,
            AttemptHistory = history,
            FirstFailedAt = firstFailedAt,
            DeadLetteredAt = now,
            ReplayState = DlqReplayState.Quarantined,
        };

        await dlqRepo.AddAsync(message, ct);

        logger.LogWarning(
            "Message dead-lettered: message_id={MessageId} consumer={ConsumerName} error_class={ErrorClass}",
            envelope.MessageId,
            consumerName,
            errorClass);
    }
}
