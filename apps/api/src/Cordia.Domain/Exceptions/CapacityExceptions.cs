namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a finalize-against-hold attempt finds the hold is no longer in Active state
/// (already expired or released — expiry raced finalization and won; spec 07 §14.3).
/// The caller must route the operation to Reconciling rather than Booked.
/// </summary>
public sealed class HoldNoLongerValidException : DomainException
{
    /// <summary>The hold ID that was not found in Active state.</summary>
    public string HoldId { get; }

    public HoldNoLongerValidException(string holdId)
        : base($"Hold '{holdId}' is no longer active and cannot be finalized.")
    {
        HoldId = holdId;
    }
}

/// <summary>
/// Thrown when a capacity write would result in held + booked exceeding total capacity on a slot,
/// indicating a programming error (the conditional WHERE guard should have prevented this).
/// Maps to HTTP 409 Conflict; triggers an alert for investigation.
/// </summary>
public sealed class OvercommitAttemptedException : DomainException
{
    /// <summary>The slot ID for which an overcommit was detected.</summary>
    public string SlotId { get; }

    public OvercommitAttemptedException(string slotId)
        : base($"Attempted to overcommit slot '{slotId}': held + booked would exceed total capacity.")
    {
        SlotId = slotId;
    }
}
