"""Unit tests for the SingleFlight bounded result cache (Fix F, §27.3 / Inv-5).

Verifies that SingleFlight(max_cached=N) evicts the oldest entry (and its lock)
once the cache grows beyond N, so the cache never grows without bound.
"""

from __future__ import annotations

import pytest

from cordia_agent.recovery import SingleFlight


# ---------------------------------------------------------------------------
# Fix F — bounded SingleFlight evicts oldest entry when cache is full
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_single_flight_bounded_cache_evicts_oldest() -> None:
    """Fix F: SingleFlight(max_cached=2) must evict the oldest key when a 3rd
    distinct key is stored, keeping ``len(_results) <= 2`` at all times.
    """
    sf = SingleFlight(max_cached=2)

    sentinel_a = object()
    sentinel_b = object()
    sentinel_c = object()

    key_a, key_b, key_c = "op_fix_f_a", "op_fix_f_b", "op_fix_f_c"

    # Populate three distinct keys sequentially.
    await sf.run(key_a, lambda: _resolved(sentinel_a))
    await sf.run(key_b, lambda: _resolved(sentinel_b))
    # After this call key_a must be evicted (it was oldest).
    await sf.run(key_c, lambda: _resolved(sentinel_c))

    assert len(sf._results) <= 2, (
        f"Fix F: cache must not exceed max_cached=2; got {len(sf._results)}"
    )


@pytest.mark.unit
async def test_single_flight_bounded_cache_evicts_oldest_key_and_lock() -> None:
    """Fix F: the evicted key's lock must also be pruned from ``_locks``."""
    sf = SingleFlight(max_cached=2)

    key_a, key_b, key_c = "op_lock_a", "op_lock_b", "op_lock_c"

    await sf.run(key_a, lambda: _resolved("r_a"))
    await sf.run(key_b, lambda: _resolved("r_b"))
    await sf.run(key_c, lambda: _resolved("r_c"))

    # key_a was first inserted → should be evicted.
    assert key_a not in sf._results, (
        "Fix F: oldest key (key_a) must be evicted from _results"
    )
    assert key_a not in sf._locks, (
        "Fix F: oldest key (key_a) lock must be pruned from _locks"
    )


@pytest.mark.unit
async def test_single_flight_bounded_cache_retains_two_most_recent() -> None:
    """Fix F: after eviction, the two most-recently-stored keys are still cached."""
    sf = SingleFlight(max_cached=2)

    sentinel_b = object()
    sentinel_c = object()

    key_a, key_b, key_c = "op_ret_a", "op_ret_b", "op_ret_c"

    await sf.run(key_a, lambda: _resolved("gone"))
    result_b = await sf.run(key_b, lambda: _resolved(sentinel_b))
    result_c = await sf.run(key_c, lambda: _resolved(sentinel_c))

    assert result_b is sentinel_b
    assert result_c is sentinel_c

    # Both recent keys still queryable (no second factory call).
    factory_called = []

    async def _unexpected():
        factory_called.append(True)
        return object()

    cached_b = await sf.run(key_b, _unexpected)
    cached_c = await sf.run(key_c, _unexpected)

    assert not factory_called, "Fix F: factory must NOT be called for still-cached keys"
    assert cached_b is sentinel_b
    assert cached_c is sentinel_c


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


async def _resolved(value: object) -> object:
    """Trivial async factory that returns *value* immediately."""
    return value
