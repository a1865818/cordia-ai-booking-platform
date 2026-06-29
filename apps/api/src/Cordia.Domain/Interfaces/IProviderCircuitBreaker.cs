namespace Cordia.Domain.Interfaces;

/// <summary>
///   Per-(provider, account) circuit breaker that protects against flooding an unhealthy provider
///   (spec 06 §15.4, §6.3 CircuitCheck step).
///   <para>
///     This interface is intentionally in-process per-instance. Multi-instance deployments would
///     need shared state (e.g. Redis); that is a follow-up concern — keep it minimal, no new dependency.
///   </para>
/// </summary>
public interface IProviderCircuitBreaker
{
    /// <summary>
    ///   Returns <see langword="true"/> when the circuit is open and calls should be suppressed.
    ///   Returns <see langword="false"/> when closed or in half-open probe state (one probe is allowed through).
    /// </summary>
    bool IsOpen(string providerName, string providerAccount);

    /// <summary>Records a successful call; resets failure counter and closes the circuit if half-open.</summary>
    void RecordSuccess(string providerName, string providerAccount);

    /// <summary>Records a failed call; increments failure counter and may open the circuit.</summary>
    void RecordFailure(string providerName, string providerAccount);
}
