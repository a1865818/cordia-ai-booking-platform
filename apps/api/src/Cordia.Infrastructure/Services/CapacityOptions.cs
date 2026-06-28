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
    /// Default: 45 minutes — 30-minute deposit window plus a 15-minute buffer to ensure the
    /// hold outlives the deposit link even if the expiry job fires slightly late. The hold TTL
    /// is computed at reserve time (before the deposit session exists), so a buffer over the
    /// deposit window is required to prevent a premature expiry race (spec 07 FIX 7).
    /// Configurable via the "Capacity" config section.
    /// </summary>
    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(45);
}
