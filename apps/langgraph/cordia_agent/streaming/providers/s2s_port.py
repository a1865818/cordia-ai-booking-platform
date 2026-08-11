"""Provider-facing S2S (Speech-to-Speech) event types and port (§6.2, OPTIONAL/DEFERRED).

This module defines the S2SProvider protocol and its dataclass event types.
It is NOT on the Track A critical path — the mainline STT→agent→TTS cascade
(pipeline.py) operates independently of this module.

No real provider dependency is imported here.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator


@dataclass(frozen=True, slots=True)
class S2SPartialTranscript:
    """Interim user speech recognition hypothesis (NON-committable)."""

    text: str
    stability: float


@dataclass(frozen=True, slots=True)
class S2SFinalTranscript:
    """Final user utterance from the S2S provider (committable → transcript.final)."""

    text: str
    asr_confidence: float


@dataclass(frozen=True, slots=True)
class S2SAgentText:
    """Agent spoken response text from the S2S provider (committable → agent.text.final)."""

    text: str


@dataclass(frozen=True, slots=True)
class S2SAudioChunk:
    """Opaque reference to agent audio output (→ tts.audio.delta)."""

    audio_ref: str


@dataclass(frozen=True, slots=True)
class S2STurnCancelled:
    """Barge-in or interrupt signal from the S2S provider (→ turn.cancelled, NON-authoritative)."""


type S2SProviderEvent = (
    S2SPartialTranscript
    | S2SFinalTranscript
    | S2SAgentText
    | S2SAudioChunk
    | S2STurnCancelled
)


class S2SProvider(Protocol):
    """Streaming speech-to-speech provider port (§6.2, OPTIONAL/DEFERRED).

    Implementations receive live PCM audio and yield mixed transcription and
    agent-response events. No real provider dependency should be imported in
    implementations — tests use the deterministic fake in ``tests/_stubs``.
    """

    def converse(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[S2SProviderEvent]:
        """Yield provider events from an async stream of PCM chunks."""
