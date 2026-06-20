"""Sentence/phrase chunking adapter for streaming TTS."""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.providers import TTSCancellationToken, TTSProvider
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    TtsAudioDeltaFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator

    from cordia_agent.streaming.turn_manager import TurnManager


type AgentTextFrame = AgentTextDeltaFrame | AgentTextFinalFrame

_BOUNDARY_RE = re.compile(r"([.?!;:])(\s+|$)")


@dataclass(frozen=True, slots=True)
class ActiveTTSTurn:
    """A cancellable assistant TTS turn."""

    turn_id: str
    cancellation: TTSCancellationToken


class TTSChunker:
    """Convert streamed assistant text into sentence-sized TTS audio frames."""

    def __init__(self, provider: TTSProvider, turn_manager: TurnManager) -> None:
        self._provider = provider
        self._turn_manager = turn_manager
        self._buffer = ""
        self._text_seen = ""
        self._chunk_seq = 0
        self._active_turn: ActiveTTSTurn | None = None

    @property
    def active_turn(self) -> ActiveTTSTurn | None:
        return self._active_turn

    def cancel_active_turn(self) -> str | None:
        """Cancel active synthesis and return the assistant turn id, if any."""
        if self._active_turn is None:
            return None

        self._active_turn.cancellation.cancel()
        return self._active_turn.turn_id

    async def process(
        self, frames: AsyncIterable[AgentTextFrame]
    ) -> AsyncIterator[TtsAudioDeltaFrame]:
        """Yield TTS audio frames as soon as text boundaries are available."""
        async for frame in frames:
            async for tts_frame in self.process_frame(frame):
                yield tts_frame

    async def process_frame(
        self, frame: AgentTextFrame
    ) -> AsyncIterator[TtsAudioDeltaFrame]:
        """Process one agent text frame and yield zero or more TTS frames."""
        if isinstance(frame, AgentTextDeltaFrame):
            self._buffer += frame.delta
            self._text_seen += frame.delta
            chunks = self._pop_ready_chunks()
        else:
            self._buffer += self._remaining_final_text(frame.text)
            chunks = self._pop_all_chunks()

        for chunk in chunks:
            async for tts_frame in self._synthesize_chunk(chunk, frame):
                yield tts_frame

    def _pop_ready_chunks(self) -> list[str]:
        chunks: list[str] = []

        while match := _BOUNDARY_RE.search(self._buffer):
            end = match.end()
            chunk = self._buffer[:end].strip()
            self._buffer = self._buffer[end:]
            if chunk:
                chunks.append(chunk)

        return chunks

    def _pop_all_chunks(self) -> list[str]:
        chunks = self._pop_ready_chunks()
        tail = self._buffer.strip()
        self._buffer = ""
        if tail:
            chunks.append(tail)
        return chunks

    def _remaining_final_text(self, final_text: str) -> str:
        if not self._text_seen:
            return final_text
        if final_text.startswith(self._text_seen):
            return final_text[len(self._text_seen) :]
        return ""

    async def _synthesize_chunk(
        self, text: str, source_frame: AgentTextFrame
    ) -> AsyncIterator[TtsAudioDeltaFrame]:
        cancellation = self._active_turn.cancellation if self._active_turn else None
        if (
            self._active_turn is None
            or self._active_turn.turn_id != source_frame.turn_id
        ):
            cancellation = TTSCancellationToken()
            self._active_turn = ActiveTTSTurn(
                turn_id=source_frame.turn_id,
                cancellation=cancellation,
            )

        chunk_seq = self._chunk_seq
        self._chunk_seq += 1

        async for audio_chunk_ref in self._provider.synthesize(
            text,
            turn_id=source_frame.turn_id,
            chunk_seq=chunk_seq,
            cancellation=cancellation,
        ):
            yield TtsAudioDeltaFrame(
                type=StreamFrameType.TTS_AUDIO_DELTA,
                tenant_id=source_frame.tenant_id,
                session_id=source_frame.session_id,
                turn_id=source_frame.turn_id,
                correlation_id=source_frame.correlation_id,
                causation_id=source_frame.causation_id,
                seq=self._turn_manager.next_seq(source_frame.session_id),
                ts=datetime.now(UTC),
                audio_chunk_ref=audio_chunk_ref,
                chunk_seq=chunk_seq,
            )
