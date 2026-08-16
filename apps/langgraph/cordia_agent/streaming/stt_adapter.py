"""Adapter from streaming STT provider events to Cordia stream frames."""

from __future__ import annotations

from contextlib import aclosing
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.providers import (
    STTFinalEvent,
    STTPartialEvent,
    STTProvider,
)
from cordia_agent.streaming_models import TranscriptFinalFrame, TranscriptPartialFrame


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator

    from cordia_agent.streaming.turn_manager import TurnManager


type TranscriptFrame = TranscriptPartialFrame | TranscriptFinalFrame


@dataclass(frozen=True, slots=True)
class STTFrameEnvelope:
    """Stable envelope fields propagated onto STT transcript frames."""

    tenant_id: str
    session_id: str
    turn_id: str
    correlation_id: str
    causation_id: str | None = None


class STTAdapter:
    """Convert provider recognition events into transcript stream frames."""

    def __init__(self, provider: STTProvider, turn_manager: TurnManager) -> None:
        self._provider = provider
        self._turn_manager = turn_manager

    async def transcribe(
        self,
        pcm_chunks: AsyncIterable[bytes],
        *,
        envelope: STTFrameEnvelope,
    ) -> AsyncIterator[TranscriptFrame]:
        """Yield transcript frames for provider recognition events.

        The provider stream is wrapped in ``aclosing`` because this adapter
        RETURNS at the first final rather than draining — and abandoning an
        async generator leaves it suspended until the event loop's
        async-generator hook finalises it, which is what actually releases the
        provider's socket or HTTP response.  Closing it here makes that
        deterministic instead of load-dependent.
        """
        async with aclosing(self._provider.transcribe(pcm_chunks)) as events:
            async for event in events:
                if isinstance(event, STTPartialEvent):
                    yield self._partial_frame(event, envelope)
                    continue

                if isinstance(event, STTFinalEvent):
                    if not event.is_endpoint:
                        raise ValueError("Received non-endpoint STTFinalEvent")
                    yield self._final_frame(event, envelope)
                    return

    def _partial_frame(
        self, event: STTPartialEvent, envelope: STTFrameEnvelope
    ) -> TranscriptPartialFrame:
        return TranscriptPartialFrame(
            type=StreamFrameType.TRANSCRIPT_PARTIAL,
            tenant_id=envelope.tenant_id,
            session_id=envelope.session_id,
            turn_id=envelope.turn_id,
            correlation_id=envelope.correlation_id,
            causation_id=envelope.causation_id,
            seq=self._turn_manager.next_seq(envelope.session_id),
            ts=datetime.now(UTC),
            text=event.text,
            stability=event.stability,
        )

    def _final_frame(
        self, event: STTFinalEvent, envelope: STTFrameEnvelope
    ) -> TranscriptFinalFrame:
        return TranscriptFinalFrame(
            type=StreamFrameType.TRANSCRIPT_FINAL,
            tenant_id=envelope.tenant_id,
            session_id=envelope.session_id,
            turn_id=envelope.turn_id,
            correlation_id=envelope.correlation_id,
            causation_id=envelope.causation_id,
            seq=self._turn_manager.next_seq(envelope.session_id),
            ts=datetime.now(UTC),
            text=event.text,
            asr_confidence=_clamp_confidence(event.asr_confidence),
            is_endpoint=True,
        )


def _clamp_confidence(value: float) -> float:
    return min(max(value, 0.0), 1.0)
