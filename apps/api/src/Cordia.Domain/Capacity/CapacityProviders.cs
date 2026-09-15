namespace Cordia.Domain.Capacity;

/// <summary>Provider names the capacity ladder treats specially (spec 07 §6.3).</summary>
public static class CapacityProviders
{
    /// <summary>
    /// Cordia's own <c>capacity_slots</c> inventory. Always the <see cref="CapacityPrimitive.FirstParty"/>
    /// rung, whatever is configured.
    /// </summary>
    public const string FirstParty = "first_party";
}
