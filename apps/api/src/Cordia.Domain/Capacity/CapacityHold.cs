using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Capacity;

/// <summary>
/// Durable record of a capacity hold for a booking operation (spec 07 §4.4).
/// A hold is active until it is released (Expired, Compensated, Superseded, ProviderReleased)
/// or finalized (Finalized) against a confirmed booking.
/// </summary>
public class CapacityHold : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string HoldId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    /// <summary>Logical workflow step (e.g. "create", "modify-new-slot") that owns this hold.</summary>
    public string WorkflowStep { get; set; } = string.Empty;

    /// <summary>First-party slot this hold is against; null for external-only holds.</summary>
    public string? SlotId { get; set; }

    /// <summary>Number of capacity units reserved by this hold. Must be greater than zero.</summary>
    public int ReservedCapacity { get; set; }

    public HoldStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Reason this hold was released or finalized; null while Active or Reconciling.</summary>
    public ReleaseReason? ReleaseReason { get; set; }

    /// <summary>External booking provider reference; null for first-party holds.</summary>
    public string? ProviderRef { get; set; }

    /// <summary>Optimistic-concurrency version token; incremented on every conditional write.</summary>
    public long Version { get; set; }
}
