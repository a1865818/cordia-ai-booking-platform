"""Deterministic fake STT provider for adapter tests."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator, Iterable

    from cordia_agent.streaming.providers.types import STTProviderEvent


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
