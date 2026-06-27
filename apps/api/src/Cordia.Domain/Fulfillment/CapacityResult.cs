namespace Cordia.Domain.Fulfillment;

/// <summary>Result of a capacity reservation attempt (spec 06 §6.3, FM#8).</summary>
public enum CapacityResult
{
    /// <summary>Capacity was successfully reserved.</summary>
    Reserved,

    /// <summary>No capacity available; the operation must route to AlternativesRequired.</summary>
    NoCapacity,
}
