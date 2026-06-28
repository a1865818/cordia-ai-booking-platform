using Cordia.Domain.Messaging;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for dead-lettering a transport message after business state has been durably
/// persisted (Inv-14, spec 06 §17.2).
///
/// <para><strong>Contract:</strong> the caller MUST have already committed the business state
/// (recoverable/terminal status, ops work-item, user comms) in a SEPARATE, already-committed
/// transaction BEFORE calling this. Dead-lettering is a transport-level step that must never
/// replace or precede customer-visible business state.</para>
/// </summary>
public interface IDeadLetterHandler
{
    /// <summary>
    /// Inserts a quarantined dead-letter row for <paramref name="envelope"/>.
    /// <paramref name="attemptHistoryJson"/> should be a JSON array (defaults to <c>[]</c> if
    /// null or empty).
    /// </summary>
    Task QuarantineAsync(
        JobEnvelope envelope,
        string consumerName,
        string errorClass,
        string attemptHistoryJson,
        DateTimeOffset firstFailedAt,
        CancellationToken ct = default);
}
