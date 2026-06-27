using System.Collections.Concurrent;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
///   Per-(providerName, providerAccount) in-memory circuit breaker (spec 06 §15.4).
///   States: closed → open (after <see cref="FailureThreshold"/> consecutive failures)
///           → half-open (after <see cref="OpenDuration"/> elapses, one probe allowed)
///           → closed (probe succeeds) or open (probe fails).
///   <para>
///     This implementation is in-process per-instance. Multi-instance deployments would need
///     shared state (e.g. Redis); that is a follow-up concern.
///   </para>
/// </summary>
public sealed class InMemoryProviderCircuitBreaker : IProviderCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly Func<DateTimeOffset> _now;

    private readonly ConcurrentDictionary<(string, string), CircuitState> _states = new();

    /// <param name="failureThreshold">Consecutive failures required to open the circuit. Default: 5.</param>
    /// <param name="openDuration">How long the circuit stays open before entering half-open. Default: 30 s.</param>
    /// <param name="now">Clock factory for testability; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryProviderCircuitBreaker(
        int failureThreshold = 5,
        TimeSpan openDuration = default,
        Func<DateTimeOffset>? now = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration == default ? TimeSpan.FromSeconds(30) : openDuration;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public bool IsOpen(string providerName, string providerAccount)
    {
        var key = (providerName, providerAccount);
        if (!_states.TryGetValue(key, out var state))
            return false; // no state yet → closed

        return state.CheckIsOpen(_now(), _openDuration);
    }

    /// <inheritdoc/>
    public void RecordSuccess(string providerName, string providerAccount)
    {
        _states.AddOrUpdate(
            (providerName, providerAccount),
            _ => CircuitState.Closed(),
            (_, existing) => existing.WithSuccess(_now(), _openDuration));
    }

    /// <inheritdoc/>
    public void RecordFailure(string providerName, string providerAccount)
    {
        _states.AddOrUpdate(
            (providerName, providerAccount),
            _ => CircuitState.FirstFailure(_now(), _failureThreshold),
            (_, existing) => existing.WithFailure(_now(), _failureThreshold));
    }

    // ------------------------------------------------------------------
    // Internal state record
    // ------------------------------------------------------------------

    private sealed class CircuitState
    {
        private enum State { Closed, Open, HalfOpen }

        // This object is a DI singleton mutated by many workers concurrently. Each per-key
        // CircuitState is read-modify-written from IsOpen/RecordSuccess/RecordFailure, so every
        // transition method runs entirely inside this gate. All transitions mutate `this` in place
        // and return `this` (never a fresh instance) so the lock identity stays stable for the
        // lifetime of the dictionary entry.
        private readonly object _gate = new();

        private State _state;
        private int _consecutiveFailures;
        private DateTimeOffset _openedAt;
        private bool _halfOpenProbeDispatched;

        private CircuitState() { }

        public static CircuitState Closed() => new() { _state = State.Closed };

        public static CircuitState FirstFailure(DateTimeOffset now, int threshold)
        {
            var s = new CircuitState { _consecutiveFailures = 1 };
            if (s._consecutiveFailures >= threshold)
            {
                s._state = State.Open;
                s._openedAt = now;
            }
            return s;
        }

        /// <summary>
        ///   Returns true when the circuit is open and should block calls.
        ///   Returns false when closed or when the circuit is half-open and a probe has not yet been dispatched
        ///   (i.e. returns false exactly once to let a single probe through — §15.4 "do not flood").
        /// </summary>
        public bool CheckIsOpen(DateTimeOffset now, TimeSpan openDuration)
        {
            lock (_gate)
            {
                if (_state == State.Closed) return false;

                if (_state == State.Open)
                {
                    if (now - _openedAt >= openDuration)
                    {
                        // Transition to half-open; allow one probe.
                        _state = State.HalfOpen;
                        _halfOpenProbeDispatched = false;
                    }
                    else
                    {
                        return true; // still open
                    }
                }

                // Half-open: allow the first probe through, block subsequent calls. Because this
                // read-and-set runs inside the lock, exactly one concurrent caller observes
                // _halfOpenProbeDispatched == false and returns false.
                if (!_halfOpenProbeDispatched)
                {
                    _halfOpenProbeDispatched = true;
                    return false; // let the probe through
                }

                return true; // probe already dispatched; block until it resolves
            }
        }

        public CircuitState WithSuccess(DateTimeOffset now, TimeSpan openDuration)
        {
            // Any success in half-open or closed → reset to closed (mutate in place).
            lock (_gate)
            {
                _state = State.Closed;
                _consecutiveFailures = 0;
                _halfOpenProbeDispatched = false;
                _openedAt = default;
            }
            return this;
        }

        public CircuitState WithFailure(DateTimeOffset now, int threshold)
        {
            lock (_gate)
            {
                if (_state == State.HalfOpen)
                {
                    // Probe failed → re-open.
                    _state = State.Open;
                    _openedAt = now;
                    _halfOpenProbeDispatched = false;
                    return this;
                }

                // Already open: do not re-stamp _openedAt (that would indefinitely extend cooldown).
                if (_state == State.Open) return this;

                _consecutiveFailures++;
                if (_consecutiveFailures >= threshold)
                {
                    _state = State.Open;
                    _openedAt = now;
                    _halfOpenProbeDispatched = false;
                }

                return this;
            }
        }
    }
}
