"""Correlation contextvar propagation -- Python ambient for spec 13 §3.

Provides a module-level ``ContextVar`` that carries the current
``CorrelationContext`` across ``await`` boundaries (Python's ContextVar is
automatically propagated by asyncio tasks, so any coroutine that inherits the
task's copy_context sees the same binding).

Fail-open contract: every getter returns ``None`` rather than raising when no
context has been bound.  Callers must tolerate ``None`` -- observability must
never break the agent.

Spec refs: §3 (correlation context flow), §4.1 (cross-service header set),
§8 (fail-open).
"""

from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar
from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import Generator
    from contextvars import Token

    from cordia_agent.observability.correlation import CorrelationContext

# ---------------------------------------------------------------------------
# Module-level ContextVar -- the Python AsyncLocal equivalent.
# ---------------------------------------------------------------------------

_correlation_var: ContextVar[CorrelationContext | None] = ContextVar(
    "_correlation_var", default=None
)


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------


def get_correlation() -> CorrelationContext | None:
    """Return the currently bound ``CorrelationContext``, or ``None``.

    Never raises -- fail-open per §8.

    Returns:
        The current context or ``None`` if nothing has been set.
    """
    return _correlation_var.get()


def set_correlation(ctx: CorrelationContext) -> Token[CorrelationContext | None]:
    """Bind *ctx* as the current correlation context and return the reset token.

    The caller is responsible for calling ``token.var.reset(token)`` if they
    need to restore the previous value.  Prefer :func:`bind_correlation` for
    scoped use.

    Args:
        ctx: The ``CorrelationContext`` to install.

    Returns:
        A ``contextvars.Token`` that can be used to undo the binding.
    """
    return _correlation_var.set(ctx)


@contextmanager
def bind_correlation(
    ctx: CorrelationContext,
) -> Generator[CorrelationContext, None, None]:
    """Context-manager that sets *ctx* on enter and resets the token on exit.

    Designed for scoping a graph node or a single agent turn so that nested
    coroutines automatically see the right context.

    Args:
        ctx: The ``CorrelationContext`` to install for the lifetime of the block.

    Yields:
        The bound ``CorrelationContext`` (same object as *ctx*).

    Example::

        async with bind_correlation(ctx):
            await some_node(state)
    """
    token = _correlation_var.set(ctx)
    try:
        yield ctx
    finally:
        _correlation_var.reset(token)


def current_correlation_id() -> str | None:
    """Return the ``correlation_id`` of the active context, or ``None``.

    Convenience wrapper around :func:`get_correlation` -- fail-open; never
    raises.

    Returns:
        The correlation ID string, or ``None`` if no context is bound.
    """
    ctx = _correlation_var.get()
    return ctx.correlation_id if ctx is not None else None


def set_causation(causation_id: str) -> None:
    """Advance the ``causation_id`` on the current context in-place.

    Creates a copy of the active ``CorrelationContext`` with the new
    ``causation_id`` and replaces the contextvar binding.  If no context is
    currently bound, this is a no-op (fail-open).

    The model is not frozen, so ``model_copy(update=...)`` is used to produce
    the updated copy without mutating the caller's reference.

    Args:
        causation_id: The new causation identifier (the ID of the current
            span/message that will become the cause of the next hop).
    """
    ctx = _correlation_var.get()
    if ctx is None:
        return
    updated = ctx.model_copy(update={"causation_id": causation_id})
    _correlation_var.set(updated)
