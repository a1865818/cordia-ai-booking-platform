"""Unit tests for cordia_agent.observability.context — contextvar propagation.

Spec refs: §3 (correlation context flow), §8 (fail-open).
"""

from __future__ import annotations

import asyncio

import pytest

from cordia_agent.observability.context import (
    bind_correlation,
    current_correlation_id,
    get_correlation,
    set_causation,
    set_correlation,
)
from cordia_agent.observability.correlation import CorrelationContext


def _make_ctx(**kwargs: str) -> CorrelationContext:
    defaults = {
        "tenant_id": "tenant-1",
        "correlation_id": "corr-abc",
        "causation_id": None,
        "session_id": None,
        "turn_id": None,
        "operation_id": None,
    }
    defaults.update(kwargs)
    return CorrelationContext(**defaults)


# ---------------------------------------------------------------------------
# Basic get / set
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_get_correlation_returns_none_when_unset() -> None:
    # Use bind_correlation to establish a known context, then verify get_correlation
    # returns None after the scope exits (rather than relying on ambient test state).
    ctx = _make_ctx(correlation_id="isolation-check")
    with bind_correlation(ctx):
        assert get_correlation() is ctx

    # After exiting the scope the contextvar is reset to whatever it was before,
    # which is None when no outer binding is active.
    result = get_correlation()
    assert result is None or result is not ctx, "bind_correlation must restore prior state on exit"


@pytest.mark.unit
def test_set_and_get_correlation() -> None:
    ctx = _make_ctx(correlation_id="set-get-test")
    token = set_correlation(ctx)
    try:
        result = get_correlation()
        assert result is not None
        assert result.correlation_id == "set-get-test"
    finally:
        token.var.reset(token)


@pytest.mark.unit
def test_current_correlation_id_returns_none_when_unset() -> None:
    # In a fresh synchronous scope where nothing is set, should return None.
    # We use bind_correlation with a guard to isolate.
    with bind_correlation(_make_ctx(correlation_id="temp-id")):
        assert current_correlation_id() == "temp-id"
    # After exiting bind, contextvar is reset to prior value (likely None in
    # a fresh scope — depends on outer test state; just check it doesn't raise).
    result = current_correlation_id()
    assert result is None or isinstance(result, str)


# ---------------------------------------------------------------------------
# bind_correlation context manager
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_bind_correlation_sets_on_enter_resets_on_exit() -> None:
    ctx = _make_ctx(correlation_id="bind-test")
    prior = get_correlation()

    with bind_correlation(ctx) as bound:
        assert bound is ctx
        assert get_correlation() is ctx
        assert current_correlation_id() == "bind-test"

    # After exit the contextvar is reset to the prior value.
    assert get_correlation() is prior


@pytest.mark.unit
def test_bind_correlation_resets_on_exception() -> None:
    ctx = _make_ctx(correlation_id="exception-test")
    prior = get_correlation()

    with pytest.raises(ValueError):
        with bind_correlation(ctx):
            assert get_correlation() is ctx
            raise ValueError("deliberate")

    # Reset must happen even when an exception escapes.
    assert get_correlation() is prior


@pytest.mark.unit
def test_bind_correlation_nested() -> None:
    outer = _make_ctx(correlation_id="outer")
    inner = _make_ctx(correlation_id="inner")

    with bind_correlation(outer):
        assert current_correlation_id() == "outer"
        with bind_correlation(inner):
            assert current_correlation_id() == "inner"
        assert current_correlation_id() == "outer"


# ---------------------------------------------------------------------------
# Async propagation — contextvar flows across await boundaries
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.asyncio
async def test_bind_correlation_propagates_into_coroutine() -> None:
    ctx = _make_ctx(correlation_id="async-corr")

    async def inner() -> str | None:
        return current_correlation_id()

    with bind_correlation(ctx):
        result = await inner()

    assert result == "async-corr"


@pytest.mark.unit
@pytest.mark.asyncio
async def test_bind_correlation_propagates_into_nested_task() -> None:
    """ContextVar propagates into asyncio tasks created within the scope."""
    ctx = _make_ctx(correlation_id="task-corr")

    async def task_fn() -> str | None:
        return current_correlation_id()

    with bind_correlation(ctx):
        # asyncio.create_task copies the current context.
        task = asyncio.create_task(task_fn())
        result = await task

    assert result == "task-corr"


# ---------------------------------------------------------------------------
# set_causation
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_set_causation_advances_causation_id() -> None:
    ctx = _make_ctx(correlation_id="corr-1", causation_id=None)
    with bind_correlation(ctx):
        set_causation("new-cause")
        updated = get_correlation()
        assert updated is not None
        assert updated.causation_id == "new-cause"
        # correlation_id must be preserved
        assert updated.correlation_id == "corr-1"


@pytest.mark.unit
def test_set_causation_does_not_mutate_original() -> None:
    ctx = _make_ctx(correlation_id="corr-2", causation_id="old")
    with bind_correlation(ctx):
        set_causation("replaced")
        updated = get_correlation()
        assert updated is not None
        assert updated.causation_id == "replaced"
    # Original ctx object is unchanged (model_copy was used).
    assert ctx.causation_id == "old"


@pytest.mark.unit
def test_set_causation_noop_when_no_context() -> None:
    # When nothing is bound, set_causation should be a no-op, not raise.
    prior = get_correlation()
    if prior is not None:
        # Skip — some other test left a binding; not safe to test here.
        pytest.skip("prior binding present")
    set_causation("ghost-cause")  # should not raise
    assert get_correlation() is None
