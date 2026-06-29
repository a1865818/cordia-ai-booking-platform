"""Scripted fake implementing the streaming AgentPort for pipeline tests."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import AsyncIterator, Iterable

    from cordia_agent.streaming import AgentOutputFrame
    from cordia_agent.streaming_models import TranscriptFinalFrame


class FakeAgent:
    """Yield a frozen script of agent output frames for one final transcript."""

    def __init__(self, script: Iterable[AgentOutputFrame]) -> None:
        self._script = tuple(script)

    async def process(
        self, frame: TranscriptFinalFrame
    ) -> AsyncIterator[AgentOutputFrame]:
        for item in self._script:
            yield item
