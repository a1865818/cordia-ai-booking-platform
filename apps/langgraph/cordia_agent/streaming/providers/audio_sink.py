"""Media-plane audio sink port.

``TTSProvider.synthesize`` deliberately yields **opaque refs, not bytes** — raw
audio stays out of band on the media plane (see ``tts_port``).  But nothing in
this repo owns a media plane yet: no component produces the inbound
``pcm_chunks`` and none resolves a ``tts://`` ref back to bytes.

``AudioSink`` is the seam that keeps that decision deferred.  A synthesis
provider hands each freshly produced chunk to a sink and yields whatever
opaque ref the sink returns; whether that ref later resolves to a Twilio media
stream write, a WebRTC track, or a blob-store object is entirely the sink
implementation's business, and can be chosen without touching the providers.

The port is intentionally minimal: one async write returning one ref.  It is a
port only — the real implementation lands with the media plane, and the test
double lives in ``tests/_stubs/audio_sink_fake.py``.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True, slots=True)
class AudioChunkMetadata:
    """Identifying metadata accompanying one synthesized audio chunk.

    Carries everything a media plane needs to route and order a chunk without
    ever inspecting the audio itself.

    Attributes:
        turn_id: The assistant turn the audio belongs to.  A barge-in cancels
            by turn, so the sink can drop everything for a cancelled turn.
        chunk_seq: The text-chunk ordinal within the turn, as assigned by
            ``TTSChunker`` (one sentence-sized text chunk per value).
        frame_seq: Monotonic ordinal of this audio frame **within** the text
            chunk, starting at 0.  One text chunk streams as many frames.
        content_type: IANA-style media type of ``audio``, e.g. ``audio/pcm``.
        sample_rate_hz: Sample rate of ``audio`` in hertz.  Needed because the
            provider's native rate (24 kHz for ``gpt-4o-mini-tts``) does not
            match PSTN's 8 kHz — the sink or the media plane owns any
            resampling.
    """

    turn_id: str
    chunk_seq: int
    frame_seq: int
    content_type: str
    sample_rate_hz: int


class AudioSink(Protocol):
    """Port accepting raw audio bytes and returning an opaque reference.

    Implementations MUST NOT log, echo, or otherwise expose the audio bytes;
    they are caller speech or agent speech and are treated as sensitive.
    """

    async def write(self, audio: bytes, metadata: AudioChunkMetadata) -> str:
        """Persist or forward one audio chunk and return its opaque reference.

        Args:
            audio: Raw encoded audio for a single chunk.  Never logged.
            metadata: Routing and ordering metadata for the chunk.

        Returns:
            An opaque reference string suitable for
            ``TtsAudioDeltaFrame.audio_chunk_ref``.  Callers treat it as
            unstructured; only the sink knows how to resolve it.

        Raises:
            Exception: implementation-defined; a failure to place audio on the
                media plane propagates to the synthesis provider, which does
                not swallow it.
        """
        ...
