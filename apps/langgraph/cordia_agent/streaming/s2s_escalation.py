"""S2S escalation adapter — §6.2, OPTIONAL/DEFERRED.

Maps a live S2SProvider event stream to the §6.3 stream-frame contract, preserving
the committable-frame discipline (Inv-16) and envelope propagation (Inv-13).

This adapter is NOT on the Track A critical path. The default Pipeline
(pipeline.py) operates the STT→agent→TTS cascade independently — this module
is never imported by pipeline.py.

Usage: construct S2SEscalationAdapter explicitly and call ``engage`` to
activate. There is no implicit wiring.
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.providers.s2s_port import (
    S2SAgentText,
    S2SAudioChunk,
    S2SFinalTranscript,
    S2SPartialTranscript,
    S2STurnCancelled,
)
from cordia_agent.streaming_models import (
    AgentTextFinalFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator

    from cordia_agent.streaming.providers.s2s_port import S2SProvider
    from cordia_agent.streaming.turn_manager import StreamFrame, TurnManager


class S2SEscalationAdapter:
    """Map S2SProvider events to §6.3 stream frames (§6.2, OPTIONAL/DEFERRED).

    Preserves:
    - Envelope propagation (Inv-13): tenant_id/session_id/turn_id/correlation_id
      passed to ``engage`` are forwarded UNCHANGED into every emitted frame.
    - Committable discipline (Inv-16): only S2SFinalTranscript and S2SAgentText
      produce committable frames (TranscriptFinalFrame, AgentTextFinalFrame).
      S2STurnCancelled produces a TurnCancelledFrame with
      ``material_content_delivered=False`` and marks the turn non-authoritative
      via ``turn_manager.handle_cancellation``.
    - Monotonic seq: each frame's ``seq`` is assigned via
      ``turn_manager.next_seq(session_id)`` (shared with the rest of the session).

    This adapter is opt-in only. It cannot be engaged without an explicit
    S2SProvider — do NOT wire it into Pipeline / pipeline.py.
    """

    def __init__(self, *, provider: S2SProvider, turn_manager: TurnManager) -> None:
        self._provider = provider
        self._turn_manager = turn_manager

    async def engage(
        self,
        pcm_chunks: AsyncIterable[bytes],
        *,
        tenant_id: str,
        session_id: str,
        turn_id: str,
        correlation_id: str,
        causation_id: str | None = None,
    ) -> AsyncIterator[StreamFrame]:
        """Drive one S2S escalation turn, yielding §6.3 stream frames.

        Envelope fields (tenant_id, session_id, turn_id, correlation_id) are
        propagated UNCHANGED into every emitted frame (Inv-13).

        Args:
            pcm_chunks: Live PCM audio input.
            tenant_id: Opaque tenant scope.
            session_id: Identifies one call/conversation.
            turn_id: Identifies the escalated turn.
            correlation_id: Correlation id from channel ingress.
            causation_id: Optional event-lineage parent id.
        """
        audio_chunk_seq = 0

        async for event in self._provider.converse(pcm_chunks):
            now = datetime.now(UTC)
            seq = self._turn_manager.next_seq(session_id)

            if isinstance(event, S2SPartialTranscript):
                yield TranscriptPartialFrame(
                    type=StreamFrameType.TRANSCRIPT_PARTIAL,
                    tenant_id=tenant_id,
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=correlation_id,
                    causation_id=causation_id,
                    seq=seq,
                    ts=now,
                    text=event.text,
                    stability=event.stability,
                )

            elif isinstance(event, S2SFinalTranscript):
                yield TranscriptFinalFrame(
                    type=StreamFrameType.TRANSCRIPT_FINAL,
                    tenant_id=tenant_id,
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=correlation_id,
                    causation_id=causation_id,
                    seq=seq,
                    ts=now,
                    text=event.text,
                    asr_confidence=event.asr_confidence,
                )

            elif isinstance(event, S2SAgentText):
                yield AgentTextFinalFrame(
                    type=StreamFrameType.AGENT_TEXT_FINAL,
                    tenant_id=tenant_id,
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=correlation_id,
                    causation_id=causation_id,
                    seq=seq,
                    ts=now,
                    text=event.text,
                )

            elif isinstance(event, S2SAudioChunk):
                yield TtsAudioDeltaFrame(
                    type=StreamFrameType.TTS_AUDIO_DELTA,
                    tenant_id=tenant_id,
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=correlation_id,
                    causation_id=causation_id,
                    seq=seq,
                    ts=now,
                    audio_chunk_ref=event.audio_ref,
                    chunk_seq=audio_chunk_seq,
                )
                audio_chunk_seq += 1

            elif isinstance(event, S2STurnCancelled):
                frame = TurnCancelledFrame(
                    type=StreamFrameType.TURN_CANCELLED,
                    tenant_id=tenant_id,
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=correlation_id,
                    causation_id=causation_id,
                    seq=seq,
                    ts=now,
                    cancelled_turn_id=turn_id,
                    material_content_delivered=False,
                )
                self._turn_manager.handle_cancellation(frame)
                yield frame
