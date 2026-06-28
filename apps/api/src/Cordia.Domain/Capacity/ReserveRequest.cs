namespace Cordia.Domain.Capacity;

/// <summary>
/// Input to <see cref="ICapacityReservation.ReserveAsync"/>. Carries the identifiers and
/// constraints needed to perform a conditional capacity write and create an active hold.
/// </summary>
/// <param name="TenantId">Tenant that owns this reservation.</param>
/// <param name="OperationId">Booking operation the hold is attached to.</param>
/// <param name="WorkflowStep">Logical step name (e.g. "create"); disambiguates holds within one operation.</param>
/// <param name="SlotId">First-party slot to reserve against; null routes to an external adapter.</param>
/// <param name="RequestedCapacity">Number of capacity units to reserve (must be &gt; 0).</param>
/// <param name="ExpiresAt">Absolute timestamp at which the hold expires if not finalized.</param>
/// <param name="ProviderName">Provider ladder key; use "first_party" for first-party slots.</param>
public record ReserveRequest(
    string TenantId,
    string OperationId,
    string WorkflowStep,
    string? SlotId,
    int RequestedCapacity,
    DateTimeOffset ExpiresAt,
    string ProviderName);
