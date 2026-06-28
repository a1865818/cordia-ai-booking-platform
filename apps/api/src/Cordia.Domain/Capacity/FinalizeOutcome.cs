namespace Cordia.Domain.Capacity;

/// <summary>
/// Result of <see cref="ICapacityReservation.FinalizeAgainstHoldAsync"/>.
/// </summary>
public enum FinalizeOutcome
{
    /// <summary>
    /// The active hold was atomically transitioned to Finalized and the slot's
    /// held capacity was moved to booked capacity.
    /// </summary>
    Finalized,

    /// <summary>
    /// The hold was no longer in Active state (already expired or released);
    /// the operation must route to Reconciling rather than proceed to Booked (§14.3).
    /// </summary>
    HoldNoLongerValid,
}
