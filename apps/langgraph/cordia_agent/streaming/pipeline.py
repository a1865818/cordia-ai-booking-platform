"""Session orchestrator wiring ingress → STT → agent → TTS for one call."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.security.redaction import redact_text
from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.barge_in import BargeInController
from cordia_agent.streaming.progress_filler import ProgressFiller
from cordia_agent.streaming.stt_adapter import STTAdapter, STTFrameEnvelope
from cordia_agent.streaming.tts_chunker import TTSChunker
from cordia_agent.streaming.turn_manager import TurnManager
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    AudioStartedFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator

    from cordia_agent.streaming.agent_port import AgentOutputFrame, AgentPort
    from cordia_agent.streaming.providers import STTProvider, TTSProvider
    from cordia_agent.streaming.transcript_persister import TranscriptPersister
    from cordia_agent.streaming.turn_manager import StreamFrame


import logging


_log = logging.getLogger(__name__)


def _scrub_transcript_frame(frame: StreamFrame) -> StreamFrame:
    """Return a copy of a transcript frame with its text redacted (Inv-12)."""
    if isinstance(frame, TranscriptPartialFrame | TranscriptFinalFrame):
        scrubbed = redact_text(frame.text)
        if scrubbed != frame.text:
            return frame.model_copy(update={"text": scrubbed})
    return frame


@dataclass(frozen=True, slots=True)
class PipelineSession:
    """Stable session-level identity for one pipeline run."""

    tenant_id: str
    session_id: str
    turn_id: str
    correlation_id: str
    causation_id: str | None = None


class Pipeline:
    """Connect the streaming sub-components behind one shared TurnManager."""

    def __init__(
        self,
        *,
        stt_provider: STTProvider,
        tts_provider: TTSProvider,
        agent: AgentPort,
        turn_manager: TurnManager | None = None,
        transcript_persister: TranscriptPersister | None = None,
    ) -> None:
        self._turn_manager = turn_manager or TurnManager()
        self._stt = STTAdapter(provider=stt_provider, turn_manager=self._turn_manager)
        self._tts = TTSChunker(provider=tts_provider, turn_manager=self._turn_manager)
        self._barge_in = BargeInController(
            tts_chunker=self._tts, turn_manager=self._turn_manager
        )
        self._filler = ProgressFiller(turn_manager=self._turn_manager)
        self._agent = agent
        # Spec 14 §4.3: optional side port persisting committed SCRUBBED finals; None ⇒
        # today's behavior byte-for-byte (no persistence from the pipe).
        self._transcript_persister = transcript_persister

    async def run(
        self, pcm_chunks: AsyncIterable[bytes], *, session: PipelineSession
    ) -> AsyncIterator[StreamFrame]:
        """Drive one call turn end-to-end, yielding ordered stream frames."""
        yield self._audio_started(session)

        envelope = STTFrameEnvelope(
            tenant_id=session.tenant_id,
            session_id=session.session_id,
            turn_id=session.turn_id,
            correlation_id=session.correlation_id,
            causation_id=session.causation_id,
        )
        async for transcript in self._stt.transcribe(pcm_chunks, envelope=envelope):
            # Spec 14 pre-emit redaction (Inv-12): PAN/CVV/gov-id shapes never
            # leave the model boundary inside a transcript frame — downstream
            # consumers (UI, logs, traces) only ever see the scrubbed text.
            scrubbed = _scrub_transcript_frame(transcript)
            yield scrubbed
            if isinstance(transcript, TranscriptPartialFrame):
                for frame in self._barge_in.caller_speech_started(transcript).frames:
                    yield frame
            elif isinstance(transcript, TranscriptFinalFrame):
                # §4.3: the persister only ever sees the SCRUBBED committed final; the
                # agent keeps receiving the in-boundary frame exactly as before.
                if self._transcript_persister is not None and isinstance(
                    scrubbed, TranscriptFinalFrame
                ):
                    await self._persist_final(scrubbed)
                async for frame in self._run_agent(transcript):
                    yield frame

    async def _persist_final(self, scrubbed: TranscriptFinalFrame) -> None:
        """Best-effort persist — a failure must never break the caller's turn (§8)."""
        try:
            await self._transcript_persister.persist(scrubbed)  # type: ignore[union-attr]
        except Exception:
            _log.exception(
                "transcript persist failed for session=%s turn=%s; continuing the turn",
                scrubbed.session_id,
                scrubbed.turn_id,
            )

    async def _run_agent(
        self, final: TranscriptFinalFrame
    ) -> AsyncIterator[StreamFrame]:
        async for output in self._agent.process(final):
            if isinstance(output, ToolProgressFrame):
                stamped = self._restamp_seq(output)
                yield stamped
                phrase = self._filler.fill(stamped)
                if phrase is not None:
                    async for tts in self._render_filler(stamped, phrase):
                        yield tts
            elif isinstance(output, AgentTextDeltaFrame | AgentTextFinalFrame):
                stamped = self._restamp_seq(output)
                yield stamped
                async for tts in self._tts.process_frame(stamped):
                    yield tts

    def _restamp_seq(self, frame: AgentOutputFrame) -> AgentOutputFrame:
        """Assign the next wire ``seq`` from the shared session sequencer."""
        return frame.model_copy(
            update={"seq": self._turn_manager.next_seq(frame.session_id)}
        )

    async def _render_filler(
        self, progress: ToolProgressFrame, phrase: str
    ) -> AsyncIterator[StreamFrame]:
        # Dedicated filler turn keeps audio refs/seq isolated from the assistant turn
        # and lets the chunker reset cleanly before the real agent text arrives.
        synthetic = AgentTextFinalFrame(
            type=StreamFrameType.AGENT_TEXT_FINAL,
            tenant_id=progress.tenant_id,
            session_id=progress.session_id,
            turn_id=f"{progress.turn_id}:filler:{progress.tool_name}",
            correlation_id=progress.correlation_id,
            causation_id=progress.causation_id,
            seq=0,  # not emitted; only the resulting tts.audio.delta frames are
            ts=datetime.now(UTC),
            text=phrase,
        )
        async for tts in self._tts.process_frame(synthetic):
            yield tts

    def _audio_started(self, session: PipelineSession) -> AudioStartedFrame:
        return AudioStartedFrame(
            type=StreamFrameType.AUDIO_STARTED,
            tenant_id=session.tenant_id,
            session_id=session.session_id,
            turn_id=session.turn_id,
            correlation_id=session.correlation_id,
            causation_id=session.causation_id,
            seq=self._turn_manager.next_seq(session.session_id),
            ts=datetime.now(UTC),
        )
