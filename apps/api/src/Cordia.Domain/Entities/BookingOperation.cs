using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Domain.Entities;

/// <summary>Mutable booking operation row with optimistic-concurrency version token.</summary>
public class BookingOperation : ITenantScoped, ICorrelatable
{
    public string TenantId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public int IntentRevision { get; set; }

    public OperationTypeEnum OperationType { get; set; }

    public OperationStatus Status { get; set; }

    public string? SupersededOperationId { get; set; }

    public string? BookingId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }

    /// <summary>Durable terminal/recoverable reason (§4, §11). Set on every transition that carries one.</summary>
    public string? StatusReason { get; set; }

    /// <summary>Set when a cancellation is requested while an operation is in-flight (§15.5).</summary>
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    /// <summary>Ambient correlation id stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation id lineage stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CausationId { get; set; }

    /// <summary>Guarded in-memory transition (§11): validates the edge then mutates Status/StatusReason/UpdatedAt.
    /// Throws <see cref="InvalidStateTransitionException"/> on an undefined edge. Persistence is done separately via the
    /// repository's version-guarded TryTransitionStatusAsync; this method does NOT bump Version.</summary>
    public void TransitionTo(OperationStatus next, string? reason = null)
    {
        OperationStateMachine.EnsureAllowed(Status, next);
        Status = next;
        StatusReason = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
