"""Provider-facing STT event types."""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol


if TYPE_CHECKING:
    from collections.abc import AsyncIterable, AsyncIterator


@dataclass(frozen=True, slots=True)
class STTPartialEvent:
    """Provider interim recognition hypothesis."""

    text: str
    stability: float


@dataclass(frozen=True, slots=True)
class STTFinalEvent:
    """Provider final recognition result for an utterance endpoint."""

    text: str
    asr_confidence: float
    is_endpoint: bool = True


type STTProviderEvent = STTPartialEvent | STTFinalEvent


class STTProvider(Protocol):
    """Streaming speech-to-text provider port."""

    def transcribe(
        self, pcm_chunks: AsyncIterable[bytes]
    ) -> AsyncIterator[STTProviderEvent]:
        """Yield provider recognition events from an async stream of PCM chunks."""
