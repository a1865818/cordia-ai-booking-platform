"""Shared HTTP bounding policy for the Azure audio providers.

Audio calls follow the same shape as ``llm/invoke.ainvoke_with_timeout``: every
attempt is bounded, and the budget GROWS per retry because a first-attempt
timeout is usually a cold deployment or a transient stall, so retrying on the
same budget would just fail the same way.

Retrying is safe here for the same reason it is safe for a model call (Inv-5):
transcription and synthesis are PURE READS.  Neither leaves a durable side
effect, so a duplicated request costs latency and tokens, never correctness.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx


if TYPE_CHECKING:
    from collections.abc import Iterator


# Seconds added to the per-attempt budget for each subsequent attempt.
DEFAULT_TIMEOUT_INCREMENT = 4.0

# Total attempts per audio call.  Lower than the model's 3: a caller is waiting
# on the line in real time, so a third attempt would usually arrive after the
# turn is already lost.
DEFAULT_RETRIES = 2

# Failures worth a second attempt.  ``httpx.TimeoutException`` and
# ``httpx.TransportError`` cover connect/read/write timeouts and connection
# resets; ``TimeoutError`` covers our own ``asyncio.wait_for`` deadline.  A
# non-2xx response is NOT retried — a 401 or 400 will fail identically.
RETRYABLE_ERRORS: tuple[type[BaseException], ...] = (
    TimeoutError,
    httpx.TimeoutException,
    httpx.TransportError,
)


def attempt_timeouts(
    base_timeout: float,
    *,
    increment: float = DEFAULT_TIMEOUT_INCREMENT,
    retries: int = DEFAULT_RETRIES,
) -> Iterator[float]:
    """Yield the per-attempt deadline for each attempt, growing by *increment*.

    Args:
        base_timeout: First-attempt deadline in seconds.
        increment: Seconds added to the budget per subsequent attempt.
        retries: Total number of attempts; must be >= 1.

    Yields:
        The deadline in seconds for each successive attempt.

    Raises:
        ValueError: if *retries* < 1 or *base_timeout* <= 0.
    """
    if retries < 1:
        raise ValueError("retries must be >= 1")
    if base_timeout <= 0:
        raise ValueError("base_timeout must be > 0")
    for attempt in range(retries):
        yield base_timeout + attempt * increment
