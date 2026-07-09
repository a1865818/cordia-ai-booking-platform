using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.DeadLetters;

/// <summary>
/// Command to replay a quarantined dead-letter message (spec 05 §6.1, §10, T7).
/// The handler applies the §6.1 ID rules exactly. An operator action (spec 14
/// §24.5): requires an actor identity (X-Actor) and a reason code, and the
/// replay is audited with both.
/// </summary>
public sealed record ReplayDeadLetterCommand(
    string TenantId,
    string MessageId,
    string ConsumerName,
    string ActorId,
    string ReasonCode)
    : IRequest<ReplayDeadLetterResult>, IOperatorActionRequest
{
    public string ActionName => "dead_letter.replayed";

    public string TargetType => "dead_letter_message";

    public string TargetId => MessageId;
}

/// <summary>Outcome of the §6.1 replay decision. Serialized by member name (string) in API responses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReplayDecision
{
    /// <summary>Message was never admitted — original message_id preserved (§6.1 row 1).</summary>
    PreservedOriginalMessageId,

    /// <summary>Workflow step already admitted — new control message-id used (§6.1 row 2).</summary>
    NewControlMessageId,

    /// <summary>Inbox already records success — replay acks without re-admit (§6.1 row 3).</summary>
    AckedWithoutReadmit,
}

/// <summary>Result returned from the replay handler.</summary>
/// <param name="Decision">Which §6.1 branch was taken.</param>
/// <param name="ReplayCommandId">New audit-only ID for this replay operation (always new).</param>
/// <param name="DispatchedMessageId">
/// The message_id of the envelope that was dispatched to the transport, or null when
/// <see cref="ReplayDecision.AckedWithoutReadmit"/> (no publish).
/// </param>
public sealed record ReplayDeadLetterResult(
    ReplayDecision Decision,
    string ReplayCommandId,
    string? DispatchedMessageId);
