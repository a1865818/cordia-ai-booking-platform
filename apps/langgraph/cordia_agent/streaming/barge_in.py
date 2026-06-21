"""Barge-in cancellation helper for assistant TTS playback."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming_models import (
    BargeInFrame,
    TranscriptPartialFrame,
    TurnCancelledFrame,
)


if TYPE_CHECKING:
    from cordia_agent.streaming.tts_chunker import TTSChunker
    from cordia_agent.streaming.turn_manager import TurnManager


type BargeInOutputFrame = BargeInFrame | TurnCancelledFrame


@dataclass(frozen=True, slots=True)
class BargeInResult:
    """Frames emitted for a caller barge-in event."""

    frames: tuple[BargeInOutputFrame, ...]
    cancelled_turn_id: str | None


class BargeInController:
    """Cancel active assistant speech when caller speech starts."""

    def __init__(self, *, tts_chunker: TTSChunker, turn_manager: TurnManager) -> None:
        self._tts_chunker = tts_chunker
        self._turn_manager = turn_manager

    def caller_speech_started(self, frame: TranscriptPartialFrame) -> BargeInResult:
        """Emit barge-in/cancellation frames when speech interrupts active TTS."""
        cancelled_turn_id = self._tts_chunker.cancel_active_turn()
        if cancelled_turn_id is None:
            return BargeInResult(frames=(), cancelled_turn_id=None)

        barge_in = BargeInFrame(
            type=StreamFrameType.BARGE_IN_DETECTED,
            tenant_id=frame.tenant_id,
            session_id=frame.session_id,
            turn_id=frame.turn_id,
            correlation_id=frame.correlation_id,
            causation_id=frame.causation_id,
            seq=self._turn_manager.next_seq(frame.session_id),
            ts=datetime.now(UTC),
        )
        cancellation = TurnCancelledFrame(
            type=StreamFrameType.TURN_CANCELLED,
            tenant_id=frame.tenant_id,
            session_id=frame.session_id,
            turn_id=frame.turn_id,
            correlation_id=frame.correlation_id,
            causation_id=frame.causation_id,
            seq=self._turn_manager.next_seq(frame.session_id),
            ts=datetime.now(UTC),
            cancelled_turn_id=cancelled_turn_id,
            material_content_delivered=False,
        )
        self._turn_manager.handle_cancellation(cancellation)

        return BargeInResult(
            frames=(barge_in, cancellation),
            cancelled_turn_id=cancelled_turn_id,
        )
