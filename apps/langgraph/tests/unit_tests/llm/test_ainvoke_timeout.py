"""Unit tests for the bounded model-invocation wrapper.

Timing is driven by a fake runnable that sleeps for a scripted duration, so the
tests assert the retry/deadline SHAPE without ever touching a provider.
"""

from __future__ import annotations

import asyncio
from typing import Any

import pytest
from langchain_core.messages import AIMessage

from cordia_agent.llm.invoke import (
    EmptyModelResponse,
    ainvoke_with_timeout,
    check_empty_response,
)


class ScriptedModel:
    """Fake runnable replaying a per-attempt script of sleeps and results.

    Each entry is either a float (sleep that long, then return ``ok``), an
    exception instance (raise it), or a ``BaseMessage`` (return immediately).
    """

    def __init__(self, script: list[Any]) -> None:
        self._script = list(script)
        self.attempts = 0
        self.timeouts_seen: list[float] = []
        self.configs: list[Any] = []

    async def ainvoke(self, input: Any, config: Any = None) -> AIMessage:
        step = self._script[min(self.attempts, len(self._script) - 1)]
        self.attempts += 1
        self.configs.append(config)
        if isinstance(step, Exception):
            raise step
        if isinstance(step, AIMessage):
            return step
        await asyncio.sleep(step)
        return AIMessage(content="ok")


async def test_returns_first_successful_attempt() -> None:
    model = ScriptedModel([0.0])

    result = await ainvoke_with_timeout(model, "hi", base_timeout=1.0)

    assert result.content == "ok"
    assert model.attempts == 1


async def test_retries_after_timeout_then_succeeds() -> None:
    """First attempt overruns its budget; the retry runs on a larger one."""
    model = ScriptedModel([1.0, 0.0])

    result = await ainvoke_with_timeout(
        model, "hi", base_timeout=0.05, increment=0.2, retries=3
    )

    assert result.content == "ok"
    assert model.attempts == 2


async def test_raises_timeout_after_exhausting_retries() -> None:
    model = ScriptedModel([5.0])

    with pytest.raises(TimeoutError):
        await ainvoke_with_timeout(
            model, "hi", base_timeout=0.01, increment=0.01, retries=3
        )

    assert model.attempts == 3


async def test_budget_grows_per_attempt() -> None:
    """A retry on the SAME budget would just fail identically — it must grow.

    The scripted sleep sits between the second and third budgets, so the call
    can only succeed if attempt 3 got a strictly larger deadline than attempt 2.
    """
    model = ScriptedModel([0.25, 0.25, 0.0])

    result = await ainvoke_with_timeout(
        model, "hi", base_timeout=0.05, increment=0.1, retries=3
    )

    assert result.content == "ok"
    assert model.attempts == 3


async def test_non_timeout_error_propagates_immediately() -> None:
    """Inv-5 shape: only timeouts retry; a real error is the node's decision."""
    model = ScriptedModel([ValueError("bad request")])

    with pytest.raises(ValueError, match="bad request"):
        await ainvoke_with_timeout(model, "hi", base_timeout=1.0, retries=3)

    assert model.attempts == 1


async def test_config_is_forwarded_so_retries_stay_in_one_trace() -> None:
    model = ScriptedModel([0.0])
    config = {"callbacks": [], "tags": ["slot_extractor"]}

    await ainvoke_with_timeout(model, "hi", base_timeout=1.0, config=config)

    assert model.configs == [config]


async def test_retries_below_one_rejected() -> None:
    with pytest.raises(ValueError, match="retries must be >= 1"):
        await ainvoke_with_timeout(ScriptedModel([0.0]), "hi", retries=0)


async def test_timed_out_attempt_is_cancelled() -> None:
    """wait_for cancels the attempt, so a stalled call cannot leak into the next."""
    cancelled = asyncio.Event()

    class CancelWatcher:
        async def ainvoke(self, input: Any, config: Any = None) -> AIMessage:
            try:
                await asyncio.sleep(10)
            except asyncio.CancelledError:
                cancelled.set()
                raise
            return AIMessage(content="never")

    with pytest.raises(TimeoutError):
        await ainvoke_with_timeout(
            CancelWatcher(), "hi", base_timeout=0.01, increment=0.0, retries=1
        )

    assert cancelled.is_set()


# ---------------------------------------------------------------------------
# check_empty_response
# ---------------------------------------------------------------------------


def test_plain_text_content_is_usable() -> None:
    check_empty_response(AIMessage(content="a table for four"))


def test_empty_string_content_rejected() -> None:
    with pytest.raises(EmptyModelResponse):
        check_empty_response(AIMessage(content=""))


def test_tool_calls_alone_are_usable() -> None:
    """A reasoning+tool_call turn carries no text block but is not empty."""
    message = AIMessage(
        content="",
        tool_calls=[{"name": "check_candidate_availability", "args": {}, "id": "1"}],
    )

    check_empty_response(message)


def test_responses_text_block_is_usable() -> None:
    check_empty_response(AIMessage(content=[{"type": "text", "text": "seven pm"}]))


def test_responses_empty_text_block_rejected() -> None:
    with pytest.raises(EmptyModelResponse):
        check_empty_response(AIMessage(content=[{"type": "text", "text": ""}]))


def test_reasoning_only_block_without_summary_rejected() -> None:
    """A reasoning-only turn with no answer is as empty as an empty string."""
    with pytest.raises(EmptyModelResponse):
        check_empty_response(AIMessage(content=[{"type": "reasoning", "summary": []}]))


def test_reasoning_block_with_summary_is_usable() -> None:
    check_empty_response(
        AIMessage(content=[{"type": "reasoning", "summary": ["thought"]}])
    )


def test_unknown_block_type_counts_as_output() -> None:
    check_empty_response(AIMessage(content=[{"type": "function_call", "name": "f"}]))
