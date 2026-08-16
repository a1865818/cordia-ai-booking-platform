"""Unit tests for the media-plane resampler and G.711 companding.

The interesting assertions here are the ones a "does it produce bytes" test
would miss: that an out-of-band tone is REJECTED rather than folded down into
the audible band, and that cutting the same stream into different chunks
produces byte-identical output.  Those two are the whole reason the module
exists — a naive decimator passes neither.
"""

from __future__ import annotations

import array
import math

import pytest

from cordia_agent.streaming.providers.audio_codec import (
    PcmResampler,
    decode_mulaw,
    encode_mulaw,
    pcm16_to_wav,
    wav_to_pcm16,
)


def tone(
    frequency_hz: float, rate_hz: int, seconds: float, amplitude: int = 12000
) -> bytes:
    """Render a mono 16-bit sine wave."""
    samples = array.array("h")
    for index in range(int(rate_hz * seconds)):
        samples.append(
            int(amplitude * math.sin(2.0 * math.pi * frequency_hz * index / rate_hz))
        )
    return samples.tobytes()


def rms(pcm: bytes) -> float:
    """Root-mean-square amplitude of 16-bit PCM."""
    samples = array.array("h")
    samples.frombytes(pcm)
    if not samples:
        return 0.0
    return math.sqrt(sum(value * value for value in samples) / len(samples))


# ---------------------------------------------------------------------------
# Rate conversion
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    ("source", "target"),
    [(24000, 8000), (24000, 16000), (16000, 8000), (8000, 16000), (24000, 24000)],
)
def test_output_length_matches_the_rate_ratio(source: int, target: int) -> None:
    """One second in is one second out, at the target rate."""
    resampler = PcmResampler(source_rate_hz=source, target_rate_hz=target)
    out = resampler.resample(tone(440, source, 1.0))
    assert abs(len(out) // 2 - target) <= 1


@pytest.mark.unit
def test_passband_tone_survives_downsampling() -> None:
    """A 440 Hz tone is well inside the 8 kHz band and must come through intact."""
    source = tone(440, 24000, 0.5)
    out = PcmResampler(source_rate_hz=24000, target_rate_hz=8000).resample(source)
    assert rms(out) == pytest.approx(rms(source), rel=0.05)


@pytest.mark.unit
def test_out_of_band_tone_is_rejected_not_aliased() -> None:
    """A 10 kHz tone must be filtered away, NOT folded down to 2 kHz.

    This is the assertion that separates a real anti-aliasing filter from
    naive decimation.  Decimating 24 kHz by 3 without filtering maps 10 kHz
    onto 24000/3 - 10000 = -2 kHz, i.e. an audible 2 kHz buzz at full
    amplitude.  With the filter it should be down in the noise.
    """
    source = tone(10000, 24000, 0.5)
    out = PcmResampler(source_rate_hz=24000, target_rate_hz=8000).resample(source)

    assert rms(out) < rms(source) / 100, (
        "out-of-band energy survived: the stopband is not doing its job, and a "
        "naive decimator would alias it into the voice band"
    )


@pytest.mark.unit
def test_chunking_does_not_change_the_output() -> None:
    """The same stream cut differently resamples to the same bytes.

    A media plane hands over whatever the provider's HTTP chunking produced.
    If chunk boundaries were audible, every call would sound different.
    """
    source = tone(700, 24000, 0.3)

    whole = PcmResampler(source_rate_hz=24000, target_rate_hz=16000).resample(source)

    chunked_resampler = PcmResampler(source_rate_hz=24000, target_rate_hz=16000)
    chunked = b"".join(
        chunked_resampler.resample(source[offset : offset + 997])
        for offset in range(0, len(source), 997)
    )

    assert whole == chunked


@pytest.mark.unit
def test_odd_byte_is_carried_across_chunks() -> None:
    """A chunk split mid-sample must not lose or invent a sample."""
    source = tone(440, 16000, 0.2)
    resampler = PcmResampler(source_rate_hz=16000, target_rate_hz=8000)

    # Deliberately odd split points, so the first chunk ends mid-sample.
    first = resampler.resample(source[:1001])
    second = resampler.resample(source[1001:])

    reference = PcmResampler(source_rate_hz=16000, target_rate_hz=8000).resample(source)
    assert first + second == reference


@pytest.mark.unit
def test_identity_conversion_passes_bytes_through() -> None:
    """Matching rates short-circuit rather than running the filter."""
    resampler = PcmResampler(source_rate_hz=16000, target_rate_hz=16000)
    assert resampler.is_identity is True

    source = tone(440, 16000, 0.05)
    assert resampler.resample(source) == source


@pytest.mark.unit
def test_empty_input_is_not_an_error() -> None:
    """Providers legitimately emit empty chunks; they must be inert."""
    resampler = PcmResampler(source_rate_hz=24000, target_rate_hz=8000)
    assert resampler.resample(b"") == b""


@pytest.mark.unit
def test_flush_discards_a_truncated_sample_rather_than_clicking() -> None:
    """A stream ending mid-sample must not zero-extend into a click."""
    resampler = PcmResampler(source_rate_hz=24000, target_rate_hz=8000)
    resampler.resample(tone(440, 24000, 0.05) + b"\x01")
    assert resampler.flush() == b""


@pytest.mark.unit
@pytest.mark.parametrize(
    ("source", "target", "taps"),
    [(0, 8000, 16), (24000, 0, 16), (-1, 8000, 16), (24000, 8000, 0)],
)
def test_invalid_configuration_fails_closed(
    source: int, target: int, taps: int
) -> None:
    """A nonsensical conversion raises at construction, not mid-call."""
    with pytest.raises(ValueError):
        PcmResampler(source_rate_hz=source, target_rate_hz=target, taps_per_phase=taps)


# ---------------------------------------------------------------------------
# G.711 mu-law
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_mulaw_halves_the_byte_rate() -> None:
    """One mu-law byte per 16-bit sample."""
    pcm = tone(440, 8000, 0.1)
    assert len(encode_mulaw(pcm)) == len(pcm) // 2


@pytest.mark.unit
def test_mulaw_round_trip_is_close_to_the_original() -> None:
    """Companding is lossy but must stay a recognisable version of the signal."""
    pcm = tone(440, 8000, 0.1)
    restored = decode_mulaw(encode_mulaw(pcm))

    original = array.array("h")
    original.frombytes(pcm)
    recovered = array.array("h")
    recovered.frombytes(restored)

    assert len(recovered) == len(original)
    error = math.sqrt(
        sum((a - b) ** 2 for a, b in zip(original, recovered, strict=True))
        / len(original)
    )
    # mu-law's quantisation error is roughly 1% of full scale at this amplitude.
    assert error < 0.02 * 32768


#: G.711 encodes zero twice — 0x7F is "negative zero" and 0xFF is "positive
#: zero", and both decode to linear 0.  Every other code round-trips exactly.
MULAW_NEGATIVE_ZERO = 0x7F
MULAW_POSITIVE_ZERO = 0xFF


@pytest.mark.unit
def test_mulaw_decode_then_encode_is_the_identity_except_negative_zero() -> None:
    """Every G.711 code survives a decode/encode round trip, bar negative zero.

    ``WavFileMediaPlane`` leans on this: it stores the decoded linear form in
    the recording and re-compands on the way back out, so a resolved ref
    returns the bytes that were delivered.  The one exception is inaudible —
    both zero codes are silence — but it is real, so it is pinned here rather
    than asserted away.
    """
    codes = bytes(range(256))
    round_tripped = encode_mulaw(decode_mulaw(codes))

    assert round_tripped[MULAW_NEGATIVE_ZERO] == MULAW_POSITIVE_ZERO
    for code in range(256):
        if code == MULAW_NEGATIVE_ZERO:
            continue
        assert round_tripped[code] == code, f"code 0x{code:02X} did not round-trip"


@pytest.mark.unit
def test_mulaw_clips_rather_than_wrapping() -> None:
    """Full-scale input must saturate, never wrap around to the opposite sign."""
    extremes = array.array("h", [32767, -32768, 32767, -32768]).tobytes()
    restored = decode_mulaw(encode_mulaw(extremes))
    samples = array.array("h")
    samples.frombytes(restored)
    assert samples[0] > 30000 and samples[1] < -30000


@pytest.mark.unit
def test_mulaw_drops_a_truncated_trailing_sample() -> None:
    """An odd byte count is a truncated sample, not a half sample."""
    assert len(encode_mulaw(b"\x00\x01\x02")) == 1


# ---------------------------------------------------------------------------
# WAV containers
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_wav_round_trip_preserves_pcm_and_rate() -> None:
    """The container is transparent."""
    pcm = tone(440, 8000, 0.05)
    recovered, rate = wav_to_pcm16(pcm16_to_wav(pcm, sample_rate_hz=8000))
    assert recovered == pcm
    assert rate == 8000


@pytest.mark.unit
def test_wav_reader_rejects_a_format_it_cannot_represent() -> None:
    """Stereo or 8-bit input fails loudly rather than being misread."""
    import io
    import wave

    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(2)
        handle.setsampwidth(2)
        handle.setframerate(8000)
        handle.writeframes(b"\x00\x00\x00\x00")

    with pytest.raises(ValueError, match="16-bit mono"):
        wav_to_pcm16(buffer.getvalue())
