"""Minting, single-flight dedup, and reconnect recovery for submit_intent (02-J).

Spec refs: §8.9, §10.1, §27.3, Inv-2 (mint once, never re-mint), Inv-5 (single-flight).

This module is the ONLY mint point for operation_id and idempotency_key.
All callers MUST check whether the ids are already set before calling mint_*;
the node enforces NEVER re-minting when ids are already present (Inv-2).
"""

from __future__ import annotations

import asyncio
import uuid
from collections import OrderedDict


# Bound the per-key result cache so it cannot grow without limit (one entry per
# operation_id otherwise lives forever). The dedup window is short and a resubmit
# under the same key is idempotent (the API returns duplicate_accepted → one
# operation, Inv-5), so evicting the oldest entries is safe.
_MAX_CACHED_RESULTS = 1024


# ---------------------------------------------------------------------------
# Minting helpers (Inv-2 — only place where ids are created)
# ---------------------------------------------------------------------------


def mint_operation_id() -> str:
    """Mint a new stable operation identifier.

    Returns:
        A unique ``op_<hex>`` string.  This function is the ONLY valid mint
        point for operation identifiers (Inv-2); callers MUST guard against
        calling it when an id is already set.
    """
    return f"op_{uuid.uuid4().hex}"


def mint_idempotency_key() -> str:
    """Mint a new idempotency key for a submission request.

    Returns:
        A unique ``idem_<hex>`` string.  This function is the ONLY valid mint
        point for idempotency keys (Inv-2); callers MUST guard against calling
        it when a key is already set.
    """
    return f"idem_{uuid.uuid4().hex}"


# ---------------------------------------------------------------------------
# SingleFlight — concurrent callers for the same op_id share one result (Inv-5)
# ---------------------------------------------------------------------------


class SingleFlight:
    """Per-key dedup: concurrent callers for the same key share one result.

    ``setdefault`` is atomic between awaits (no yield occurs between the
    ``setdefault`` call and acquiring the lock), so a second coroutine
    arriving before the first has released its lock will get the same
    ``asyncio.Lock`` object and will wait.  Once the first coroutine
    stores the result, the second sees it immediately after acquiring
    the lock (Inv-5).
    """

    def __init__(self, max_cached: int = _MAX_CACHED_RESULTS) -> None:
        self._locks: dict[str, asyncio.Lock] = {}
        self._results: OrderedDict[str, object] = OrderedDict()
        self._max_cached = max_cached

    async def run(self, key: str, factory) -> object:
        """Run *factory* exactly once for *key*; return the cached result on retry.

        Args:
            key: The dedup key (operation_id in submit flows).
            factory: An async callable (coroutine function / lambda returning a
                coroutine) that performs the underlying submit call.

        Returns:
            The result of the first (and only) real call to *factory* for *key*.
        """
        lock = self._locks.setdefault(key, asyncio.Lock())
        async with lock:
            if key in self._results:
                self._results.move_to_end(key)
                return self._results[key]
            result = await factory()
            self._results[key] = result
            self._results.move_to_end(key)
            # Evict the oldest entries (and their locks) once the cache is full.
            # The just-stored key is newest, so an in-flight key is never evicted.
            while len(self._results) > self._max_cached:
                evicted, _ = self._results.popitem(last=False)
                self._locks.pop(evicted, None)
            return result


# ---------------------------------------------------------------------------
# Module-global instance — shared across nodes; replaceable in tests
# ---------------------------------------------------------------------------

_single_flight: SingleFlight = SingleFlight()


def get_single_flight() -> SingleFlight:
    """Return the module-global ``SingleFlight`` instance.

    Tests call :func:`reset_single_flight` in teardown to avoid cross-test
    contamination (Inv-5 isolation).
    """
    return _single_flight


def reset_single_flight() -> None:
    """Replace the module-global ``SingleFlight`` with a fresh instance.

    Call in test teardown to ensure each test starts with a clean dedup table.
    """
    global _single_flight
    _single_flight = SingleFlight()


# ---------------------------------------------------------------------------
# Reconnect recovery helper
# ---------------------------------------------------------------------------


async def recover_operation(operation_id: str) -> object:
    """Thin wrapper: poll get_operation_status for *operation_id*.

    Kept here so the node imports a single recovery surface.  The real HTTP
    call lives in client_binding.get_operation_status_call.

    Args:
        operation_id: The stable operation identifier to poll.

    Returns:
        The ``ToolResultEnvelope`` from ``get_operation_status_call``.
    """
    from cordia_agent.tools.client_binding import (
        get_operation_status_call,
    )

    return await get_operation_status_call(operation_id)
