namespace Cordia.Application.Common.Authorization;

/// <summary>
/// Marker for owner/operator MediatR requests subject to the spec-14
/// authorization spine (§3.1, §24.5): a non-empty actor identity and reason
/// code are mandatory, and denied attempts are audited by
/// <c>OperatorActionBehavior</c>. The ALLOW audit is written by the handler
/// itself inside the same transaction as the effect (never by the behavior —
/// an audit must not claim an effect that did not commit).
///
/// Operator identity is the documented <c>X-Actor</c> shim (Stage-3 gate
/// decision 2026-07-09); the role taxonomy is a tracked open decision (08/14).
/// </summary>
public interface IOperatorActionRequest
{
    string TenantId { get; }

    /// <summary>Operator identity from the X-Actor header.</summary>
    string ActorId { get; }

    /// <summary>Mandatory reason code for the action (§24.5).</summary>
    string ReasonCode { get; }

    /// <summary>Audit action name, e.g. "dead_letter.replayed".</summary>
    string ActionName { get; }

    /// <summary>Audit target type, e.g. "dead_letter_message".</summary>
    string TargetType { get; }

    /// <summary>Audit target identifier.</summary>
    string TargetId { get; }
}
