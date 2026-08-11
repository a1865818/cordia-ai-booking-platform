"""Test-only fake for the ``AudioSink`` port.

Mirrors ``cordia_agent.streaming.providers.audio_sink``.  Records every write
and returns a deterministic ``tts://`` ref so synthesis tests stay stable and
fully offline.

DO NOT import this module from production code.  It must only be referenced
from tests and test fixtures.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.streaming.providers.audio_sink import AudioChunkMetadata


class FakeAudioSink:
    """Record written audio and return a deterministic opaque ref."""

    def __init__(self, *, scheme: str = "tts://fake") -> None:
        self._scheme = scheme
        self.writes: list[tuple[bytes, AudioChunkMetadata]] = []

    @property
    def written_bytes(self) -> bytes:
        """Concatenation of every chunk written, in write order."""
        return b"".join(audio for audio, _ in self.writes)

    async def write(self, audio: bytes, metadata: AudioChunkMetadata) -> str:
        self.writes.append((audio, metadata))
        return (
            f"{self._scheme}/{metadata.turn_id}/{metadata.chunk_seq}/"
            f"{metadata.frame_seq}"
        )


class FailingAudioSink:
    """Raise on the Nth write — proves sink failures are not swallowed."""

    def __init__(self, *, fail_on_write: int = 0) -> None:
        self._fail_on_write = fail_on_write
        self.write_count = 0

    async def write(self, audio: bytes, metadata: AudioChunkMetadata) -> str:
        index = self.write_count
        self.write_count += 1
        if index == self._fail_on_write:
            raise RuntimeError("media plane unavailable")
        return f"tts://failing/{metadata.turn_id}/{metadata.chunk_seq}/{index}"
