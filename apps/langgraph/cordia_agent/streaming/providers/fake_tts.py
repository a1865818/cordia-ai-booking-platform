"""Deterministic fake TTS provider for streaming tests."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from collections.abc import AsyncIterator

    from cordia_agent.streaming.providers.tts_port import TTSCancellationToken


class FakeTTSProvider:
    """Yield stable opaque refs for each requested text chunk."""

    def __init__(self, *, refs_per_text_chunk: int = 1) -> None:
        if refs_per_text_chunk < 1:
            raise ValueError("refs_per_text_chunk must be at least 1")
        self.refs_per_text_chunk = refs_per_text_chunk
        self.history: list[tuple[str, str, int]] = []

    async def synthesize(
        self,
        text: str,
        *,
        turn_id: str,
        chunk_seq: int,
        cancellation: TTSCancellationToken | None = None,
    ) -> AsyncIterator[str]:
        self.history.append((text, turn_id, chunk_seq))

        yielded_after_cancellation = False
        for ref_index in range(self.refs_per_text_chunk):
            if cancellation is not None and cancellation.is_cancelled:
                if yielded_after_cancellation:
                    return
                yielded_after_cancellation = True

            suffix = "" if self.refs_per_text_chunk == 1 else f"/{ref_index}"
            yield f"tts://fake/{turn_id}/{chunk_seq}{suffix}"
