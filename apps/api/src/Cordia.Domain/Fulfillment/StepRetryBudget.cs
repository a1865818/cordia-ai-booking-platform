namespace Cordia.Domain.Fulfillment;

/// <summary>
/// Per-step retry budget: maximum attempt count, maximum elapsed time, and the base/cap delays
/// used by the full-jitter exponential back-off schedule (spec 06 §15.2/15.3).
/// </summary>
/// <param name="MaxAttempts">Maximum number of attempts before budget exhaustion.</param>
/// <param name="MaxElapsed">Maximum total elapsed time from first attempt before budget exhaustion.</param>
/// <param name="BaseDelay">Base delay for exponential back-off (cap = min(MaxDelay, BaseDelay * 2^attempt)).</param>
/// <param name="MaxDelay">Upper bound on the per-attempt jitter cap.</param>
public sealed record StepRetryBudget(int MaxAttempts, TimeSpan MaxElapsed, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    /// <summary>Budget for availability-read steps (fast, shallow retries).</summary>
    public static StepRetryBudget ForAvailabilityRead =>
        new(5, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));

    /// <summary>Budget for create-booking steps (durable, deeper retries with longer back-off).</summary>
    public static StepRetryBudget ForCreateBooking =>
        new(4, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    /// <summary>Default budget (same as <see cref="ForCreateBooking"/>).</summary>
    public static StepRetryBudget Default => ForCreateBooking;
}
