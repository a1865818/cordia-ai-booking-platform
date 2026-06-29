"""Deterministic fake STT provider for adapter tests."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator, Iterable

    from cordia_agent.contracts.operation_status import NormalizedOperationStatus
    from cordia_agent.streaming.providers.types import STTProviderEvent
    from cordia_agent.streaming.reconnect import ReconnectSessionContext


class FakeSTTProvider:
    """Yield scripted recognition events, one per consumed PCM chunk."""

    def __init__(self, script: Iterable[STTProviderEvent]) -> None:
        self._script = tuple(script)

    async def transcribe(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[STTProviderEvent]:
        script_iter = iter(self._script)
        async for _chunk in pcm_chunks:
            try:
                yield next(script_iter)
            except StopIteration:
                return


class FakeSessionLookup:
    """Return a scripted session context (or None for lookup failure)."""

    def __init__(self, result: ReconnectSessionContext | None) -> None:
        self.result = result

    async def lookup(self, channel_ref: str) -> ReconnectSessionContext | None:
        return self.result


class FakeAgentResumeHook:
    """Record the resume call and return a scripted status."""

    def __init__(self, result: NormalizedOperationStatus | None) -> None:
        self.result = result
        self.called_with: ReconnectSessionContext | None = None

    async def resume(
        self, context: ReconnectSessionContext
    ) -> NormalizedOperationStatus | None:
        self.called_with = context
        return self.result


class FakeHandoffHook:
    """Record whether handoff was signalled and with what context."""

    def __init__(self) -> None:
        self.was_called = False
        self.called_with: ReconnectSessionContext | None = None

    async def signal(self, context: ReconnectSessionContext | None) -> None:
        self.was_called = True
        self.called_with = context
