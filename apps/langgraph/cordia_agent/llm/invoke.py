"""Bounded model invocation — ``ainvoke`` that can never hang (spec 03 §6.6).

Every model call in the foreground agent goes through
:func:`ainvoke_with_timeout`.  A hung provider connection would otherwise pin a
conversational turn open indefinitely, holding a caller on the line past any
useful latency budget with no route to the human-handoff path.

The retry shape follows Inv-5: a model call is a PURE READ (no durable side
effect), so retrying is safe — unlike the mutation tools in
``tools/_timeouts.py``, which never retry.  Only ``TimeoutError`` is retried;
every other exception propagates immediately so node-level policy decides.
"""

from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING, Any

from cordia_agent.llm.config import DEFAULT_BASE_TIMEOUT_SECONDS


if TYPE_CHECKING:
    from langchain_core.messages import BaseMessage
    from langchain_core.runnables import Runnable, RunnableConfig


class EmptyModelResponse(RuntimeError):
    """Raised when a model returns neither usable content nor tool calls.

    A structurally-valid but empty completion is a failure, not an answer: left
    unchecked it becomes an empty utterance to the customer or a silently
    dropped slot capture.
    """


async def ainvoke_with_timeout(
    model: Runnable[Any, BaseMessage],
    # Shadows the builtin deliberately: mirrors BaseChatModel.ainvoke's signature.
    input: Any,
    *,
    base_timeout: float | None = None,
    increment: float = 4.0,
    retries: int = 3,
    config: RunnableConfig | None = None,
) -> BaseMessage:
    """Invoke ``model.ainvoke`` under a progressive timeout so it never hangs.

    Each attempt is bounded by ``asyncio.wait_for`` on a budget that grows by
    *increment* per retry (``base_timeout``, ``base_timeout + increment``,
    ``base_timeout + 2*increment``, ...) for at most *retries* attempts.  The
    growth matters: a first-attempt timeout is usually a slow cold start or a
    transient provider stall, and retrying on the SAME budget would just fail
    the same way.

    ``asyncio.wait_for`` cancels the underlying task on timeout, so a timed-out
    attempt does not leak a connection into the next one.

    Args:
        model: Any runnable returning a ``BaseMessage`` — a chat model, or a
            chain such as ``model.with_structured_output(...)``.
        input: The invocation input, forwarded verbatim.
        base_timeout: First-attempt deadline in seconds.  ``None`` uses
            :data:`~cordia_agent.llm.config.DEFAULT_BASE_TIMEOUT_SECONDS`.
        increment: Seconds added to the budget per subsequent attempt.
        retries: Total attempts; must be >= 1.
        config: Optional ``RunnableConfig`` forwarded to ``ainvoke`` — carries
            the LangGraph callback/tracing context, so passing it through keeps
            retried attempts inside the same trace.

    Returns:
        The ``BaseMessage`` from the first attempt that completed in budget.

    Raises:
        ValueError: if *retries* < 1.
        TimeoutError: if every attempt exceeded its budget.
        Exception: anything else raised by ``ainvoke`` propagates immediately,
            unretried.
    """
    if retries < 1:
        raise ValueError("retries must be >= 1")
    if base_timeout is None:
        base_timeout = DEFAULT_BASE_TIMEOUT_SECONDS

    last_error: TimeoutError | None = None
    for attempt in range(retries):
        timeout = base_timeout + attempt * increment
        try:
            return await asyncio.wait_for(
                model.ainvoke(input, config=config), timeout=timeout
            )
        except TimeoutError as exc:
            last_error = exc
            continue

    # Only reachable once every attempt timed out (retries >= 1 guaranteed).
    raise last_error  # type: ignore[misc]


def _has_usable_content(content: Any) -> bool:
    """Return True if *content* carries any real output.

    Handles both response shapes: the Chat Completions ``str`` and the
    Responses API block list.  A block list whose only entries are
    empty-summary reasoning blocks is NOT usable — a reasoning-only turn with
    no answer and no tool call is as empty as ``""``.
    """
    if isinstance(content, str):
        return bool(content)
    if isinstance(content, list):
        for block in content:
            if isinstance(block, str):
                if block:
                    return True
            elif isinstance(block, dict):
                block_type = block.get("type")
                if block_type == "reasoning":
                    summary = block.get("summary")
                    if summary:
                        return True
                elif block_type == "text":
                    if block.get("text"):
                        return True
                else:
                    # function_call / image / any other block is real output.
                    return True
        return False
    return bool(content)


def check_empty_response(response: BaseMessage) -> None:
    """Raise :class:`EmptyModelResponse` if *response* has no usable output.

    A response counts as empty when it carries no ``tool_calls`` AND its
    ``content`` has no usable output, so a reasoning-plus-tool-call turn with
    no text block is correctly treated as non-empty via its tool calls.

    Args:
        response: The message returned by :func:`ainvoke_with_timeout`.

    Raises:
        EmptyModelResponse: if the response has neither tool calls nor content.
    """
    if not getattr(response, "tool_calls", None) and not _has_usable_content(
        getattr(response, "content", None)
    ):
        raise EmptyModelResponse(
            "model returned an empty response (no content, no tool_calls)"
        )
