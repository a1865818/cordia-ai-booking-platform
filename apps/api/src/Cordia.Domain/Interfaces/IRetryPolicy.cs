using Cordia.Domain.Fulfillment;

namespace Cordia.Domain.Interfaces;

/// <summary>Computes the next-eligible time for a retry attempt (spec 06 §15.2/15.3).</summary>
public interface IRetryPolicy
{
    /// <summary>
    ///   Returns the next eligible <see cref="DateTimeOffset"/> for the attempt that just completed,
    ///   or <see langword="null"/> when the budget (attempt count OR elapsed time) is exhausted.
    ///   Callers route to ManualReview on <see langword="null"/> (§15.3).
    /// </summary>
    /// <param name="budget">The per-step retry budget.</param>
    /// <param name="completedAttemptNumber">1-based number of the attempt that just finished.</param>
    /// <param name="firstAttemptAt">Wall-clock time of the very first attempt for this step.</param>
    /// <param name="now">Current wall-clock time (used to check elapsed budget and compute next time).</param>
    /// <returns>The next-eligible time, or <see langword="null"/> if the budget is exhausted.</returns>
    DateTimeOffset? NextEligibleAt(
        StepRetryBudget budget,
        int completedAttemptNumber,
        DateTimeOffset firstAttemptAt,
        DateTimeOffset now);
}
