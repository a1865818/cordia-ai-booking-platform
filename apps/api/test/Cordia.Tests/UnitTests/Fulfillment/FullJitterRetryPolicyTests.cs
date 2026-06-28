using Cordia.Domain.Fulfillment;
using Cordia.Infrastructure.Retry;

namespace Cordia.Tests.UnitTests.Fulfillment;

[Trait("Category", "Unit")]
public class FullJitterRetryPolicyTests
{
    private static readonly StepRetryBudget _budget = StepRetryBudget.ForCreateBooking; // MaxAttempts=4

    private readonly FullJitterRetryPolicy _policy = new();
    private readonly DateTimeOffset _firstAttemptAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReturnsNull_WhenAttemptCountReachesMax()
    {
        var now = _firstAttemptAt.AddSeconds(10);

        // completedAttemptNumber == MaxAttempts (4) → null
        var result = _policy.NextEligibleAt(_budget, _budget.MaxAttempts, _firstAttemptAt, now);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenElapsedExceedsMaxElapsed()
    {
        var now = _firstAttemptAt + _budget.MaxElapsed + TimeSpan.FromSeconds(1);

        var result = _policy.NextEligibleAt(_budget, 1, _firstAttemptAt, now);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNonNull_WhenBudgetNotExhausted()
    {
        var now = _firstAttemptAt.AddSeconds(5);

        var result = _policy.NextEligibleAt(_budget, 1, _firstAttemptAt, now);

        Assert.NotNull(result);
        Assert.True(result >= now, "Next eligible time should be >= now.");
    }

    [Fact]
    public void ReturnedDelay_IsWithinJitterBounds_OverManyIterations()
    {
        var now = _firstAttemptAt.AddSeconds(5);
        const int iterations = 1000;
        int attemptNumber = 2; // mid-range

        // cap = min(MaxDelay=30s, BaseDelay=1s * 2^2=4s) = 4s
        var capMs = Math.Min(
            _budget.MaxDelay.TotalMilliseconds,
            _budget.BaseDelay.TotalMilliseconds * Math.Pow(2, attemptNumber));

        for (int i = 0; i < iterations; i++)
        {
            var result = _policy.NextEligibleAt(_budget, attemptNumber, _firstAttemptAt, now);

            Assert.NotNull(result);
            Assert.True(result >= now, "Delay must not be negative.");
            Assert.True(result <= now.AddMilliseconds(capMs + 1), // +1ms tolerance
                $"Returned time {result} exceeds now + cap ({now.AddMilliseconds(capMs)}).");
        }
    }

    [Fact]
    public void Cap_GrowsWithAttemptNumber_BoundedByMaxDelay()
    {
        var now = _firstAttemptAt.AddSeconds(5);

        // Attempt 1 cap: min(30s, 1s*2^1) = 2s
        var result1 = _policy.NextEligibleAt(_budget, 1, _firstAttemptAt, now);
        // Attempt 2 cap: min(30s, 1s*2^2) = 4s
        var result2 = _policy.NextEligibleAt(_budget, 2, _firstAttemptAt, now);
        // Attempt 3 cap: min(30s, 1s*2^3) = 8s
        var result3 = _policy.NextEligibleAt(_budget, 3, _firstAttemptAt, now);

        // All should return non-null (budget not exhausted: attempt < MaxAttempts=4, elapsed < MaxElapsed=10min).
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotNull(result3);

        // Maximum possible values should respect the cap growth.
        // (We can't guarantee ordering due to randomness but we can verify they don't exceed MaxDelay.)
        Assert.True(result1 <= now + _budget.MaxDelay + TimeSpan.FromMilliseconds(1));
        Assert.True(result2 <= now + _budget.MaxDelay + TimeSpan.FromMilliseconds(1));
        Assert.True(result3 <= now + _budget.MaxDelay + TimeSpan.FromMilliseconds(1));
    }
}
