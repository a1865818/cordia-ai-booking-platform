using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Retry;

/// <summary>
///   Full-jitter exponential back-off retry policy (spec 06 §15.2/15.3).
///   Cap = min(MaxDelay, BaseDelay * 2^completedAttemptNumber); delay = random(0, cap).
///   Returns <see langword="null"/> when the attempt-count or elapsed-time budget is exhausted
///   (caller routes to ManualReview per §15.3).
///   Registered as a singleton.
/// </summary>
public sealed class FullJitterRetryPolicy : IRetryPolicy
{
    // Maximum bit-shift before clamping to avoid overflow (2^62 overflows long).
    private const int MaxShift = 62;

    /// <inheritdoc/>
    public DateTimeOffset? NextEligibleAt(
        StepRetryBudget budget,
        int completedAttemptNumber,
        DateTimeOffset firstAttemptAt,
        DateTimeOffset now)
    {
        // Budget exhausted: attempt count.
        if (completedAttemptNumber >= budget.MaxAttempts)
            return null;

        // Budget exhausted: elapsed time.
        if (now - firstAttemptAt >= budget.MaxElapsed)
            return null;

        // cap = min(MaxDelay, BaseDelay * 2^completedAttemptNumber), clamped to avoid overflow.
        var shift = Math.Min(completedAttemptNumber, MaxShift);
        var multiplier = 1L << shift; // 2^shift
        var capTicks = Math.Min(
            budget.MaxDelay.Ticks,
            (long)Math.Min((double)budget.BaseDelay.Ticks * multiplier, (double)budget.MaxDelay.Ticks));

        // Full jitter: uniform random in [0, cap).
        var delayTicks = (long)(Random.Shared.NextDouble() * capTicks);
        var delay = TimeSpan.FromTicks(Math.Max(0, delayTicks));

        return now + delay;
    }
}
