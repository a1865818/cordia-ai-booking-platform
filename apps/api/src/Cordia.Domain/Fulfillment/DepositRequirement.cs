namespace Cordia.Domain.Fulfillment;

/// <summary>
/// Result of the deposit policy check for a booking operation.
/// <see cref="None"/> is returned by the default no-op policy (spec 09 owns the real impl).
/// </summary>
public sealed record DepositRequirement(bool Required, int AmountMinor, string Currency)
{
    /// <summary>Deposit is not required — proceed directly to Finalizing from Held.</summary>
    public static DepositRequirement None => new(false, 0, string.Empty);
}
