namespace Cordia.Application.Common.Audit;

/// <summary>
/// Typed input for the spec-14 audit writer (D13). Maps onto the ratified
/// spec-12 audit_events row: <see cref="Action"/> → event_type,
/// <see cref="ActorId"/> → actor, <see cref="TargetId"/> → subject_ref; every
/// other field rides in the sanitized metadata jsonb.
/// </summary>
public sealed record AuditRecord(
    string TenantId,
    string ActorType,            // customer | owner | operator | agent | system
    string ActorId,
    string Action,               // e.g. "consent.recorded", "dead_letter.replayed"
    string TargetType,           // e.g. "conversation_session", "dead_letter_message"
    string TargetId,
    string Decision,             // allow | deny
    IReadOnlyList<string> DataClasses,
    string? ReasonCode = null,   // required for owner/operator actions (§24.5)
    string? CorrelationId = null,
    string? CausationId = null,
    string? OperationId = null,
    object? Extra = null,        // action-specific structured fields (sanitized)
    DateTimeOffset? OccurredAt = null); // pass the effect's clock so audit + effect share one timestamp
