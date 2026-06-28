namespace Cordia.Infrastructure.Services;

/// <summary>
/// Options for the capacity reservation subsystem (spec 07 §13 open-decision §3).
/// Bound from the "Capacity" configuration section.
/// </summary>
public sealed class CapacityOptions
{
    public const string SectionName = "Capacity";

    /// <summary>
    /// Duration a first-party hold is kept active before the expiry scheduler fires.
    /// Default: 30 minutes — must outlive the deposit confirmation window (spec 07 §13 decision,
    /// matches <c>RequestPaymentCommandHandler.DepositWindowMinutes = 30</c>).
    /// </summary>
    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(30);
}
