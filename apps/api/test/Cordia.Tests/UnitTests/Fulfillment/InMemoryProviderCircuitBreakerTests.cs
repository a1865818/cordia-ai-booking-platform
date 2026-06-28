using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Providers;

namespace Cordia.Tests.UnitTests.Fulfillment;

[Trait("Category", "Unit")]
public class InMemoryProviderCircuitBreakerTests
{
    private const string Provider = "stub";
    private const string Account = "acct_1";
    private const string OtherAccount = "acct_2";

    private static InMemoryProviderCircuitBreaker MakeBreaker(
        int threshold = 3,
        TimeSpan openDuration = default,
        Func<DateTimeOffset>? clock = null)
    {
        return new InMemoryProviderCircuitBreaker(
            failureThreshold: threshold,
            openDuration: openDuration == default ? TimeSpan.FromSeconds(30) : openDuration,
            now: clock);
    }

    [Fact]
    public void InitialState_IsClosed()
    {
        var breaker = MakeBreaker();
        Assert.False(breaker.IsOpen(Provider, Account));
    }

    [Fact]
    public void OpensAfterFailureThreshold()
    {
        var breaker = MakeBreaker(threshold: 3);

        breaker.RecordFailure(Provider, Account);
        Assert.False(breaker.IsOpen(Provider, Account), "Should still be closed after 1 failure.");

        breaker.RecordFailure(Provider, Account);
        Assert.False(breaker.IsOpen(Provider, Account), "Should still be closed after 2 failures.");

        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account), "Should open after reaching threshold (3).");
    }

    [Fact]
    public void RecordSuccess_ResetFailures_ReturnsToClose()
    {
        var breaker = MakeBreaker(threshold: 3);

        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);
        breaker.RecordSuccess(Provider, Account);

        Assert.False(breaker.IsOpen(Provider, Account), "Success should reset to closed.");

        // Need threshold failures again to re-open.
        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);
        Assert.False(breaker.IsOpen(Provider, Account));
        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account));
    }

    [Fact]
    public void AfterOpenDuration_HalfOpenProbeAllowed_SuccessCloses()
    {
        DateTimeOffset fakeNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var breaker = MakeBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30), clock: () => fakeNow);

        // Open the circuit.
        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account));

        // Advance clock past open duration.
        fakeNow = fakeNow.AddSeconds(31);

        // First IsOpen call after duration: should return false (probe allowed through).
        Assert.False(breaker.IsOpen(Provider, Account), "Half-open probe should be allowed.");

        // Subsequent calls before probe resolves: should be open again.
        Assert.True(breaker.IsOpen(Provider, Account), "Should block until probe resolves.");

        // Probe succeeds → circuit closes.
        breaker.RecordSuccess(Provider, Account);
        Assert.False(breaker.IsOpen(Provider, Account), "Success in half-open should close circuit.");
    }

    [Fact]
    public void AfterOpenDuration_HalfOpenProbeAllowed_FailureReopens()
    {
        DateTimeOffset fakeNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var breaker = MakeBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30), clock: () => fakeNow);

        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account));

        // Advance past open duration.
        fakeNow = fakeNow.AddSeconds(31);

        // Half-open probe allowed.
        Assert.False(breaker.IsOpen(Provider, Account));

        // Probe fails → re-open.
        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account), "Failure in half-open should re-open circuit.");
    }

    [Fact]
    public void CircuitState_IsIsolatedPerProviderAccount()
    {
        var breaker = MakeBreaker(threshold: 2);

        // Fail for Account.
        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);

        Assert.True(breaker.IsOpen(Provider, Account));
        Assert.False(breaker.IsOpen(Provider, OtherAccount), "Other account should still be closed.");
    }

    [Fact]
    public void HalfOpenProbe_UnderConcurrency_LetsExactlyOneCallThrough()
    {
        DateTimeOffset fakeNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var breaker = MakeBreaker(threshold: 2, openDuration: TimeSpan.FromSeconds(30), clock: () => fakeNow);

        // Open the circuit.
        breaker.RecordFailure(Provider, Account);
        breaker.RecordFailure(Provider, Account);
        Assert.True(breaker.IsOpen(Provider, Account));

        // Advance the clock past OpenDuration so the next IsOpen transitions to half-open.
        fakeNow = fakeNow.AddSeconds(31);

        // Fire many concurrent IsOpen calls; exactly one must observe the half-open window
        // and return false (the single probe). The rest must be blocked (true).
        const int callers = 100;
        var results = new bool[callers];
        Parallel.For(0, callers, i => results[i] = breaker.IsOpen(Provider, Account));

        int probesAllowed = results.Count(open => !open);
        Assert.Equal(1, probesAllowed);
    }
}
