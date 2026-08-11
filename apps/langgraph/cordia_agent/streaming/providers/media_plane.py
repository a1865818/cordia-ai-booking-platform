"""Media-plane ``AudioSink`` implementations (spec 01 §6.2 / handoff §6.2).

``AudioSink`` was shipped as a port with no implementation, which left two
concrete holes in the voice channel: nothing accepted the synthesized bytes a
provider produced, and nothing resolved the ``tts://`` ref that came back.  This
module fills both, and puts the sample-rate conversion in the one place the
handoff doc says it belongs — the sink, not the provider.

The split
    :class:`TranscodingAudioSink` is the ``AudioSink``.  It reads the source
    format off :class:`~...audio_sink.AudioChunkMetadata` (which the provider
    already stamps), converts to the configured :class:`AudioEgress`, hands the
    result to a :class:`MediaDelivery`, and returns the opaque ref built from
    whatever locator the delivery gave back.  It knows nothing about carriage.

    :class:`MediaDelivery` is carriage.  Swapping Twilio Media Streams or a
    WebRTC track for the local recorder below is implementing one Protocol with
    two methods — no provider, chunker, or pipeline code changes.

    :class:`WavFileMediaPlane` is the reference delivery: it accumulates each
    assistant turn and writes it out as a playable WAV.  That makes the voice
    channel runnable and *audible* end-to-end without committing to a telephony
    vendor, which is exactly the decision the handoff doc flags as a product
    call rather than an engineering one.

Why the conversion is here and not in ``AzureTtsProvider``
    The provider emits its deployment's native format (24 kHz PCM) and says so
    in the metadata.  Only the sink knows what the far end can carry — 8 kHz
    mulaw for PSTN, 16 kHz PCM for a browser.  Putting the resampler here means
    one provider serves every carriage, and a carriage that needs no conversion
    pays nothing (the resampler short-circuits when the rates match).

Per-turn conversion state
    Resampling is stateful: the filter carries history across chunks, so chunk
    boundaries are inaudible.  A sink therefore keeps one
    :class:`~...audio_codec.PcmResampler` per ``(turn_id, source rate)`` and
    drops it when the turn changes — a barge-in cancels a turn mid-sentence,
    and the next turn must not start with the tail of the cancelled one in its
    filter memory.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from pathlib import Path
from typing import Literal, Protocol

from cordia_agent.streaming.providers.audio_codec import (
    PcmResampler,
    decode_mulaw,
    encode_mulaw,
    pcm16_to_wav,
    wav_to_pcm16,
)
from cordia_agent.streaming.providers.audio_sink import AudioChunkMetadata


logger = logging.getLogger(__name__)

#: Scheme prefix on every ref minted here.  Matches the ``tts://`` shape
#: ``TtsAudioDeltaFrame.audio_chunk_ref`` already documents.
DEFAULT_REF_SCHEME = "tts://"

type AudioEncoding = Literal["pcm16", "mulaw"]

_CONTENT_TYPES: dict[str, str] = {"pcm16": "audio/pcm", "mulaw": "audio/basic"}


@dataclass(frozen=True, slots=True)
class AudioEgress:
    """The format the far end of the media plane can actually carry.

    Attributes:
        sample_rate_hz: Sample rate the carriage expects, in hertz.
        encoding: ``"pcm16"`` for 16-bit signed little-endian linear PCM, or
            ``"mulaw"`` for 8-bit G.711 mu-law (what a PSTN leg carries).
    """

    sample_rate_hz: int
    encoding: AudioEncoding = "pcm16"

    def __post_init__(self) -> None:
        """Validate the combination.

        Raises:
            ValueError: on a non-positive rate or an unknown encoding.
        """
        if self.sample_rate_hz <= 0:
            raise ValueError(
                f"sample_rate_hz must be > 0 (got {self.sample_rate_hz!r})"
            )
        if self.encoding not in _CONTENT_TYPES:
            raise ValueError(
                f"unknown encoding {self.encoding!r}; "
                f"expected one of {sorted(_CONTENT_TYPES)}"
            )

    @property
    def content_type(self) -> str:
        """IANA media type for :attr:`encoding`."""
        return _CONTENT_TYPES[self.encoding]

    @classmethod
    def pstn(cls) -> AudioEgress:
        """8 kHz G.711 mu-law — the format a telephone leg carries."""
        return cls(sample_rate_hz=8000, encoding="mulaw")

    @classmethod
    def wideband(cls) -> AudioEgress:
        """16 kHz linear PCM — a WebRTC/browser-friendly default."""
        return cls(sample_rate_hz=16000, encoding="pcm16")


class MediaDelivery(Protocol):
    """Carriage for converted audio: where the bytes actually go.

    Implementations MUST NOT log or echo the audio — it is agent speech and is
    treated as sensitive, exactly as the ``AudioSink`` port requires.
    """

    async def deliver(
        self, audio: bytes, metadata: AudioChunkMetadata, egress: AudioEgress
    ) -> str:
        """Place one converted chunk on the carriage and return its locator.

        Args:
            audio: The chunk, already in *egress* format.
            metadata: Routing/ordering metadata, with ``content_type`` and
                ``sample_rate_hz`` describing the **converted** audio.
            egress: The format *audio* is in.

        Returns:
            A transport-defined locator.  The sink prefixes it with a scheme to
            form the opaque ref callers see; only this delivery can resolve it.
        """
        ...

    async def fetch(self, locator: str) -> bytes:
        """Return the bytes previously delivered under *locator*.

        Args:
            locator: A locator returned by :meth:`deliver`.

        Returns:
            The chunk's bytes in egress format.

        Raises:
            KeyError: if the locator is unknown to this delivery.
        """
        ...


class TranscodingAudioSink:
    """``AudioSink`` that converts to the carriage format and delivers.

    Construct one per session and inject it into the synthesis provider, like
    every other port in this package.
    """

    def __init__(
        self,
        *,
        delivery: MediaDelivery,
        egress: AudioEgress,
        ref_scheme: str = DEFAULT_REF_SCHEME,
    ) -> None:
        """Initialise the sink.

        Args:
            delivery: Where converted audio goes.
            egress: The format the carriage expects.
            ref_scheme: Prefix for minted refs.  Opaque to every caller.
        """
        self._delivery = delivery
        self._egress = egress
        self._ref_scheme = ref_scheme
        # One resampler per turn; see the module docstring on why it is dropped
        # rather than reused when the turn changes.
        self._resampler: PcmResampler | None = None
        self._resampler_turn: tuple[str, int] | None = None

    @property
    def egress(self) -> AudioEgress:
        """The carriage format this sink converts to."""
        return self._egress

    async def write(self, audio: bytes, metadata: AudioChunkMetadata) -> str:
        """Convert one chunk, deliver it, and return its opaque ref.

        Args:
            audio: Raw audio from the provider, in ``metadata.content_type`` at
                ``metadata.sample_rate_hz``.  Never logged.
            metadata: Routing and ordering metadata from the provider.

        Returns:
            The opaque ref for ``TtsAudioDeltaFrame.audio_chunk_ref``.

        Raises:
            ValueError: if the provider's ``content_type`` is not linear PCM or
                mu-law — a container format (wav/mp3/opus) cannot be resampled
                chunk-wise and must be requested as ``pcm`` instead.
            Exception: whatever the delivery raises.  A media-plane failure is
                not swallowed here; the provider's contract says it propagates.
        """
        converted = self._convert(audio, metadata)
        stamped = self._restamp(metadata)
        locator = await self._delivery.deliver(converted, stamped, self._egress)
        return f"{self._ref_scheme}{locator}"

    async def resolve(self, ref: str) -> bytes:
        """Resolve a ref minted by this sink back to its delivered bytes.

        The counterpart the port never had: ``TTSProvider.synthesize`` yields
        refs precisely so raw audio stays off the frame contract, but a
        consumer that wants to play, record, or assert on the audio needs a way
        back.  This is it.

        Args:
            ref: A ref previously returned by :meth:`write`.

        Returns:
            The chunk's bytes, in this sink's egress format.

        Raises:
            KeyError: if the ref was not minted by this sink, or the delivery
                no longer holds it.
        """
        if not ref.startswith(self._ref_scheme):
            raise KeyError(f"ref does not belong to this sink: {ref!r}")
        return await self._delivery.fetch(ref[len(self._ref_scheme) :])

    def _convert(self, audio: bytes, metadata: AudioChunkMetadata) -> bytes:
        """Resample and compand *audio* into the egress format."""
        linear = self._to_linear(audio, metadata)
        resampled = self._resample(linear, metadata)
        if self._egress.encoding == "mulaw":
            return encode_mulaw(resampled)
        return resampled

    @staticmethod
    def _to_linear(audio: bytes, metadata: AudioChunkMetadata) -> bytes:
        """Normalise the provider's chunk to 16-bit linear PCM."""
        content_type = metadata.content_type
        if content_type in ("audio/pcm", "audio/L16", "audio/x-raw"):
            return audio
        if content_type == "audio/basic":
            return decode_mulaw(audio)
        raise ValueError(
            f"cannot transcode {content_type!r} chunk-wise; request "
            "CORDIA_AUDIO_TTS_RESPONSE_FORMAT=pcm so the provider streams raw "
            "PCM rather than a container"
        )

    def _resample(self, linear: bytes, metadata: AudioChunkMetadata) -> bytes:
        """Convert *linear* to the egress rate, reusing per-turn filter state."""
        key = (metadata.turn_id, metadata.sample_rate_hz)
        if self._resampler_turn != key:
            self._resampler = PcmResampler(
                source_rate_hz=metadata.sample_rate_hz,
                target_rate_hz=self._egress.sample_rate_hz,
            )
            self._resampler_turn = key
        assert self._resampler is not None  # set immediately above
        return self._resampler.resample(linear)

    def _restamp(self, metadata: AudioChunkMetadata) -> AudioChunkMetadata:
        """Return *metadata* describing the CONVERTED audio, not the source."""
        return AudioChunkMetadata(
            turn_id=metadata.turn_id,
            chunk_seq=metadata.chunk_seq,
            frame_seq=metadata.frame_seq,
            content_type=self._egress.content_type,
            sample_rate_hz=self._egress.sample_rate_hz,
        )


class WavFileMediaPlane:
    """Reference :class:`MediaDelivery` that records each turn to a WAV file.

    Every delivered chunk is appended to the current turn's buffer and indexed
    so it can be fetched back individually; when the turn changes — or when the
    plane is closed — the accumulated turn is written to
    ``<directory>/<turn_id>.wav``.

    Memory is bounded by ONE turn of audio, not the call: a completed turn is
    flushed to disk and its buffer released.  Fetching a chunk from a flushed
    turn reads the slice back off the file, so refs stay resolvable for the
    whole session.

    mu-law egress is decoded back to linear PCM for the container.  The bytes
    on the wire are still mu-law — this only affects the recording, and a
    linear WAV plays in anything.
    """

    def __init__(self, directory: Path | str, *, filename_prefix: str = "") -> None:
        """Initialise the recorder.

        Args:
            directory: Where per-turn WAV files are written.  Created if absent.
            filename_prefix: Optional prefix, e.g. a session id, so several
                calls can share one directory without colliding.
        """
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=True)
        self._prefix = filename_prefix
        self._turn_id: str | None = None
        self._buffer = bytearray()
        self._egress: AudioEgress | None = None
        # locator -> (turn_id, byte offset within the turn, byte length)
        self._index: dict[str, tuple[str, int, int]] = {}
        self._files: dict[str, Path] = {}

    @property
    def files(self) -> dict[str, Path]:
        """Written WAV files by turn id, in write order."""
        return dict(self._files)

    async def deliver(
        self, audio: bytes, metadata: AudioChunkMetadata, egress: AudioEgress
    ) -> str:
        """Append *audio* to the current turn and return its locator."""
        if self._turn_id != metadata.turn_id:
            self._flush()
            self._turn_id = metadata.turn_id
            self._buffer = bytearray()
        self._egress = egress

        offset = len(self._buffer)
        self._buffer.extend(audio)

        scope = f"{self._prefix}/" if self._prefix else ""
        locator = f"{scope}{metadata.turn_id}/{metadata.chunk_seq}/{metadata.frame_seq}"
        self._index[locator] = (metadata.turn_id, offset, len(audio))
        return locator

    async def fetch(self, locator: str) -> bytes:
        """Return the bytes delivered under *locator*.

        Raises:
            KeyError: if the locator was never delivered here.
        """
        try:
            turn_id, offset, length = self._index[locator]
        except KeyError:
            raise KeyError(f"unknown media locator: {locator!r}") from None

        if turn_id == self._turn_id:
            return bytes(self._buffer[offset : offset + length])

        path = self._files.get(turn_id)
        if path is None:  # pragma: no cover - only if a flush was skipped
            raise KeyError(f"no recording for turn {turn_id!r}")
        return self._read_back(path, offset, length)

    def _read_back(self, path: Path, offset: int, length: int) -> bytes:
        """Slice a flushed turn's audio back out of its WAV file.

        For mu-law carriage the file holds the decoded linear form, so the
        slice is re-companded on the way out.  G.711 decode-then-encode is the
        identity on every code except 0x7F ("negative zero"), which comes back
        as 0xFF ("positive zero") — both are silence, so a resolved ref is
        sample-accurate even though it is not always byte-identical.
        """
        pcm, _ = wav_to_pcm16(path.read_bytes())
        if self._egress is not None and self._egress.encoding == "mulaw":
            return encode_mulaw(pcm)[offset : offset + length]
        return pcm[offset : offset + length]

    async def aclose(self) -> None:
        """Flush the in-flight turn.  Idempotent."""
        self._flush()

    def _flush(self) -> None:
        """Write the buffered turn to disk and release the buffer."""
        if self._turn_id is None or not self._buffer or self._egress is None:
            return

        pcm = (
            decode_mulaw(bytes(self._buffer))
            if self._egress.encoding == "mulaw"
            else bytes(self._buffer)
        )
        safe_turn = _safe_filename(self._turn_id)
        name = f"{self._prefix}-{safe_turn}.wav" if self._prefix else f"{safe_turn}.wav"
        path = self.directory / name
        path.write_bytes(pcm16_to_wav(pcm, sample_rate_hz=self._egress.sample_rate_hz))
        self._files[self._turn_id] = path
        logger.debug(
            "media plane wrote %d byte(s) of turn audio to %s", len(pcm), path.name
        )
        # Clearing the turn is what makes `fetch` fall through to the file for
        # everything already flushed; leaving it set would answer from the
        # buffer that was just released.
        self._turn_id = None
        self._buffer = bytearray()


def _safe_filename(value: str) -> str:
    """Reduce *value* to characters that are safe in a filename."""
    return "".join(char if char.isalnum() or char in "-_" else "_" for char in value)
