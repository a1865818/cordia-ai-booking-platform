"""Deterministic fake S2S provider for streaming tests (§6.2, OPTIONAL/DEFERRED)."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator, Iterable

    from cordia_agent.streaming.providers.s2s_port import S2SProviderEvent


class FakeS2SProvider:
    """Yield scripted S2SProviderEvents deterministically, ignoring PCM input.

    Constructed with a frozen sequence of S2SProviderEvent instances. The
    ``converse`` method yields them all in order regardless of how many PCM
    chunks are received — matching the FakeSTTProvider / FakeTTSProvider style.
    """

    def __init__(self, script: Iterable[S2SProviderEvent]) -> None:
        self._script = tuple(script)

    async def converse(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[S2SProviderEvent]:
        # Consume the pcm stream to behave like a real provider but ignore it.
        async for _chunk in pcm_chunks:
            pass
        for event in self._script:
            yield event
