"""Provider-facing TTS port types."""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING, Protocol


if TYPE_CHECKING:
    from collections.abc import AsyncIterator


@dataclass(slots=True)
class TTSCancellationToken:
    """Cooperative cancellation signal for in-flight TTS synthesis."""

    is_cancelled: bool = False

    def cancel(self) -> None:
        self.is_cancelled = True


class TTSProvider(Protocol):
    """Streaming text-to-speech provider port.

    Implementations yield opaque audio chunk references only. Raw audio bytes stay
    out of band on the media plane.
    """

    def synthesize(
        self,
        text: str,
        *,
        turn_id: str,
        chunk_seq: int,
        cancellation: TTSCancellationToken | None = None,
    ) -> AsyncIterator[str]:
        """Yield opaque audio chunk references for a text chunk."""
