using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>First-party inventory slot representing a discrete time-block of a resource (spec 07 §4.2).</summary>
public class CapacitySlot : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string SlotId { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public DateTimeOffset StartsAt { get; set; }

    /// <summary>Maximum number of concurrent reservations the slot can hold.</summary>
    public int TotalCapacity { get; set; }

    /// <summary>Number of units currently held (active holds) but not yet booked.</summary>
    public int HeldCapacity { get; set; }

    /// <summary>Number of units that have been finalized into confirmed bookings.</summary>
    public int BookedCapacity { get; set; }

    /// <summary>Optimistic-concurrency version token; incremented on every conditional write.</summary>
    public long Version { get; set; }
}
