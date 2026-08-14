"""Unit tests for the media-plane ``AudioSink`` implementation.

Covers the three things the port had no implementation for: converting a
provider's native format to what the carriage can take, delivering it, and —
the part the port never had at all — resolving a ``tts://`` ref back to bytes.
"""

from __future__ import annotations

import array
import math
from typing import TYPE_CHECKING

import pytest

from cordia_agent.streaming.providers.audio_codec import decode_mulaw, wav_to_pcm16
from cordia_agent.streaming.providers.audio_sink import AudioChunkMetadata
from cordia_agent.streaming.providers.media_plane import (
    AudioEgress,
    TranscodingAudioSink,
    WavFileMediaPlane,
)


if TYPE_CHECKING:
    from pathlib import Path


def tone(frequency_hz: float, rate_hz: int, seconds: float) -> bytes:
    samples = array.array("h")
    for index in range(int(rate_hz * seconds)):
        samples.append(
            int(12000 * math.sin(2.0 * math.pi * frequency_hz * index / rate_hz))
        )
    return samples.tobytes()


def meta(
    *,
    turn_id: str = "turn-1",
    chunk_seq: int = 0,
    frame_seq: int = 0,
    content_type: str = "audio/pcm",
    sample_rate_hz: int = 24000,
) -> AudioChunkMetadata:
    return AudioChunkMetadata(
        turn_id=turn_id,
        chunk_seq=chunk_seq,
        frame_seq=frame_seq,
        content_type=content_type,
        sample_rate_hz=sample_rate_hz,
    )


class RecordingDelivery:
    """A ``MediaDelivery`` that keeps everything it was handed."""

    def __init__(self) -> None:
        self.chunks: list[tuple[bytes, AudioChunkMetadata, AudioEgress]] = []

    async def deliver(
        self, audio: bytes, metadata: AudioChunkMetadata, egress: AudioEgress
    ) -> str:
        self.chunks.append((audio, metadata, egress))
        return f"rec/{metadata.turn_id}/{metadata.chunk_seq}/{metadata.frame_seq}"

    async def fetch(self, locator: str) -> bytes:
        for index, (audio, metadata, _) in enumerate(self.chunks):
            expected = (
                f"rec/{metadata.turn_id}/{metadata.chunk_seq}/{metadata.frame_seq}"
            )
            if expected == locator and index == index:
                return audio
        raise KeyError(locator)


# ---------------------------------------------------------------------------
# AudioEgress
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_pstn_egress_is_8k_mulaw() -> None:
    """The PSTN preset is the format a telephone leg actually carries."""
    egress = AudioEgress.pstn()
    assert (egress.sample_rate_hz, egress.encoding) == (8000, "mulaw")
    assert egress.content_type == "audio/basic"


@pytest.mark.unit
@pytest.mark.parametrize(
    ("rate", "encoding"), [(0, "pcm16"), (-8000, "pcm16"), (8000, "flac")]
)
def test_invalid_egress_fails_closed(rate: int, encoding: str) -> None:
    with pytest.raises(ValueError):
        AudioEgress(sample_rate_hz=rate, encoding=encoding)  # type: ignore[arg-type]


# ---------------------------------------------------------------------------
# TranscodingAudioSink
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_sink_downsamples_and_compands_for_pstn() -> None:
    """24 kHz PCM in, 8 kHz mu-law out — a third the samples, a byte each."""
    delivery = RecordingDelivery()
    sink = TranscodingAudioSink(delivery=delivery, egress=AudioEgress.pstn())

    source = tone(440, 24000, 0.1)  # 2400 samples
    await sink.write(source, meta())

    delivered, stamped, egress = delivery.chunks[0]
    assert len(delivered) == pytest.approx(800, abs=2), (
        "8 kHz mu-law is one byte per sample at a third of the input rate"
    )
    assert egress.encoding == "mulaw"
    assert (stamped.content_type, stamped.sample_rate_hz) == ("audio/basic", 8000), (
        "metadata must describe the CONVERTED audio, not the provider's native format"
    )


@pytest.mark.unit
async def test_sink_leaves_matching_rates_alone() -> None:
    """A carriage that wants the provider's native format pays nothing."""
    delivery = RecordingDelivery()
    sink = TranscodingAudioSink(
        delivery=delivery, egress=AudioEgress(sample_rate_hz=24000, encoding="pcm16")
    )

    source = tone(440, 24000, 0.05)
    await sink.write(source, meta())

    assert delivery.chunks[0][0] == source


@pytest.mark.unit
async def test_ref_resolves_back_to_the_delivered_bytes() -> None:
    """The counterpart the port never had: a ref you can actually redeem."""
    delivery = RecordingDelivery()
    sink = TranscodingAudioSink(
        delivery=delivery, egress=AudioEgress(sample_rate_hz=24000)
    )

    ref = await sink.write(tone(440, 24000, 0.05), meta())
    assert ref.startswith("tts://")
    assert await sink.resolve(ref) == delivery.chunks[0][0]


@pytest.mark.unit
async def test_resolving_a_foreign_ref_raises() -> None:
    sink = TranscodingAudioSink(
        delivery=RecordingDelivery(), egress=AudioEgress.wideband()
    )
    with pytest.raises(KeyError):
        await sink.resolve("https://elsewhere/chunk")


@pytest.mark.unit
async def test_chunk_boundaries_are_inaudible_across_a_turn() -> None:
    """Filter state carries between writes, so one turn resamples as one stream."""
    delivery = RecordingDelivery()
    sink = TranscodingAudioSink(
        delivery=delivery, egress=AudioEgress(sample_rate_hz=8000)
    )

    source = tone(700, 24000, 0.2)
    half = len(source) // 2
    await sink.write(source[:half], meta(frame_seq=0))
    await sink.write(source[half:], meta(frame_seq=1))
    streamed = b"".join(audio for audio, _, _ in delivery.chunks)

    from cordia_agent.streaming.providers.audio_codec import PcmResampler

    reference = PcmResampler(source_rate_hz=24000, target_rate_hz=8000).resample(source)
    assert streamed == reference


@pytest.mark.unit
async def test_a_new_turn_starts_with_clean_filter_state() -> None:
    """A barge-in cancels a turn mid-sentence; its tail must not bleed forward."""
    delivery = RecordingDelivery()
    sink = TranscodingAudioSink(
        delivery=delivery, egress=AudioEgress(sample_rate_hz=8000)
    )

    source = tone(700, 24000, 0.05)
    await sink.write(source, meta(turn_id="turn-a"))
    await sink.write(source, meta(turn_id="turn-b"))

    assert delivery.chunks[0][0] == delivery.chunks[1][0], (
        "the same audio on a fresh turn must convert identically"
    )


@pytest.mark.unit
async def test_container_formats_are_refused_with_a_usable_message() -> None:
    """wav/mp3 cannot be resampled chunk-wise; say so rather than emitting noise."""
    sink = TranscodingAudioSink(delivery=RecordingDelivery(), egress=AudioEgress.pstn())
    with pytest.raises(ValueError, match="TTS_RESPONSE_FORMAT=pcm"):
        await sink.write(b"RIFF....", meta(content_type="audio/wav"))


@pytest.mark.unit
async def test_delivery_failure_is_not_swallowed() -> None:
    """The AudioSink port says a media-plane failure propagates."""

    class Failing:
        async def deliver(self, audio: bytes, metadata: object, egress: object) -> str:
            raise RuntimeError("media plane unavailable")

        async def fetch(self, locator: str) -> bytes:  # pragma: no cover
            raise KeyError(locator)

    sink = TranscodingAudioSink(delivery=Failing(), egress=AudioEgress.pstn())  # type: ignore[arg-type]
    with pytest.raises(RuntimeError, match="media plane unavailable"):
        await sink.write(tone(440, 24000, 0.01), meta())


# ---------------------------------------------------------------------------
# WavFileMediaPlane
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_each_turn_lands_in_its_own_playable_file(tmp_path: Path) -> None:
    """A turn is flushed when the next one starts, and again on close."""
    plane = WavFileMediaPlane(tmp_path)
    sink = TranscodingAudioSink(delivery=plane, egress=AudioEgress(sample_rate_hz=8000))

    await sink.write(tone(440, 24000, 0.05), meta(turn_id="turn-a"))
    await sink.write(tone(880, 24000, 0.05), meta(turn_id="turn-b"))
    await plane.aclose()

    assert set(plane.files) == {"turn-a", "turn-b"}
    for path in plane.files.values():
        pcm, rate = wav_to_pcm16(path.read_bytes())
        assert rate == 8000
        assert len(pcm) > 0


@pytest.mark.unit
async def test_refs_stay_resolvable_after_the_turn_is_flushed(tmp_path: Path) -> None:
    """Memory is bounded to one turn, but earlier refs still redeem."""
    plane = WavFileMediaPlane(tmp_path)
    sink = TranscodingAudioSink(delivery=plane, egress=AudioEgress(sample_rate_hz=8000))

    first_ref = await sink.write(tone(440, 24000, 0.05), meta(turn_id="turn-a"))
    first_bytes = await sink.resolve(first_ref)

    await sink.write(tone(880, 24000, 0.05), meta(turn_id="turn-b"))

    assert await sink.resolve(first_ref) == first_bytes, (
        "a flushed turn's chunk must read back off its recording"
    )


@pytest.mark.unit
async def test_mulaw_recording_is_stored_as_playable_linear_pcm(tmp_path: Path) -> None:
    """The wire stays mu-law; the recording is linear so anything can play it."""
    plane = WavFileMediaPlane(tmp_path, filename_prefix="sess1")
    sink = TranscodingAudioSink(delivery=plane, egress=AudioEgress.pstn())

    ref = await sink.write(tone(440, 24000, 0.05), meta(turn_id="turn-a"))
    delivered = await sink.resolve(ref)
    await plane.aclose()

    path = plane.files["turn-a"]
    assert path.name.startswith("sess1-")
    pcm, rate = wav_to_pcm16(path.read_bytes())
    assert rate == 8000
    assert pcm == decode_mulaw(delivered)


@pytest.mark.unit
async def test_unknown_locator_raises(tmp_path: Path) -> None:
    plane = WavFileMediaPlane(tmp_path)
    with pytest.raises(KeyError):
        await plane.fetch("nope/0/0")


@pytest.mark.unit
async def test_turn_ids_unsafe_in_a_filename_are_sanitised(tmp_path: Path) -> None:
    """A turn id is opaque; it must never escape the recording directory."""
    plane = WavFileMediaPlane(tmp_path)
    sink = TranscodingAudioSink(delivery=plane, egress=AudioEgress(sample_rate_hz=8000))

    await sink.write(tone(440, 24000, 0.02), meta(turn_id="../../etc/passwd"))
    await plane.aclose()

    written = plane.files["../../etc/passwd"]
    assert written.parent == tmp_path
    assert ".." not in written.name


@pytest.mark.unit
async def test_closing_without_any_audio_is_a_no_op(tmp_path: Path) -> None:
    plane = WavFileMediaPlane(tmp_path)
    await plane.aclose()
    await plane.aclose()
    assert plane.files == {}
