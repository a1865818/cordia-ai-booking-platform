using System.Text.Json;

namespace Cordia.Domain.Messaging;

/// <summary>
/// Versioned, immutable transport envelope (§4.1, §12.2).
///
/// Envelope rules:
/// - <c>MessageId</c> is globally unique; the body is immutable once created.
/// - <c>MessageType</c> carries an explicit schema version: <c>&lt;domain&gt;.&lt;event&gt;.&lt;verb&gt;.vN</c>.
/// - <c>TenantId</c> and <c>CorrelationId</c> are present on every message.
/// - No secrets, no raw payment data / PAN (Inv-12); payloads reference authoritative records by ID.
/// - <c>AttemptHint</c> is advisory only; the authoritative attempt count lives in
///   <c>workflow_attempts</c> / <c>scheduled_jobs</c>, never in the envelope (§15.2).
/// - Consumers MUST remain backward-compatible during rolling deploys (§12.2).
/// </summary>
public sealed record JobEnvelope(
    string MessageId,
    string MessageType,
    DateTimeOffset OccurredAt,
    string TenantId,
    string CorrelationId,
    string? CausationId,
    string? OperationId,
    string? IntentId,
    int? IntentRevision,
    int AttemptHint,
    // NOTE: the spec example uses JsonDocument, but JsonElement is the value-type equivalent
    // used here to avoid IDisposable lifetime issues inside a record.
    JsonElement Payload);
