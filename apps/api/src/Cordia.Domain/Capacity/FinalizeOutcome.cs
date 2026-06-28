namespace Cordia.Domain.Capacity;

/// <summary>
/// Result of <see cref="ICapacityReservation.FinalizeAgainstOperationAsync"/>.
/// </summary>
public enum FinalizeOutcome
{
    /// <summary>
    /// The active hold was atomically transitioned to Finalized and the slot's
    /// held capacity was moved to booked capacity. Also returned when the hold
    /// was already Finalized (idempotent retry — do NOT move the slot a second time).
    /// </summary>
    Finalized,

    /// <summary>
    /// The hold was no longer in Active state (expired or released — NOT already Finalized);
    /// the operation must route to CompensationRequired rather than proceed to Booked (§14.3).
    /// The provider effect exists but there is no capacity backing — Compensate will cancel it.
    /// </summary>
    HoldNoLongerValid,

    /// <summary>
    /// No first-party hold was ever created for this operation/step (non-tracked op, mode=live).
    /// The caller should proceed to Booked as normal.
    /// </summary>
    NotTracked,
}
