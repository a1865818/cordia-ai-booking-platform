"""Sample-rate conversion and G.711 companding for the media plane.

This module closes the gap the rest of the audio stack documents and
deliberately leaves open: ``gpt-4o-mini-tts`` emits **24 kHz** 16-bit mono PCM,
the realtime transcription socket wants **16 kHz**, and PSTN carriage wants
**8 kHz mulaw**.  Nothing between them resampled, because — as
``audio_config``'s docstring says — naive decimation aliases audibly and doing
it properly needs an anti-aliasing filter.

So this is the anti-aliasing filter.  It lives here, at the media-plane seam,
rather than inside a provider: the provider's job is to produce its native
format and stamp what it produced onto :class:`AudioChunkMetadata`; converting
that to whatever the carriage wants is the sink's job (see ``media_plane``).

What "properly" means here
    :class:`PcmResampler` is a polyphase rational resampler.  For a conversion
    of ``source → target`` it factors the ratio into ``L/M`` (24 kHz → 8 kHz is
    1/3; 24 kHz → 16 kHz is 2/3), conceptually upsamples by ``L``, low-passes
    at the *lower* of the two Nyquist frequencies, and decimates by ``M``.  The
    low-pass is a windowed-sinc FIR — that is the part naive decimation skips,
    and skipping it is what folds everything above the new Nyquist back down
    into the audible band as a metallic buzz.

    The filter is **causal**: an output sample depends only on input samples at
    or before it.  That matters for a live call — there is no look-ahead, so
    the only added latency is the filter's group delay (half the taps, a
    fraction of a millisecond at these rates), not a buffered window.

Streaming, not batch
    Both the resampler and the companding helpers take arbitrary byte counts.
    A media plane receives whatever the provider's HTTP chunking happened to
    produce, which is neither frame-aligned nor even guaranteed to be an even
    number of bytes.  :class:`PcmResampler` carries a trailing odd byte and its
    filter history across calls, so a stream cut into chunks resamples to the
    same bytes as the same stream cut differently.  Feed one instance per
    stream; do not share one across concurrent turns.

No third-party dependency
    Pure ``array``/``int`` arithmetic, on purpose.  Pulling numpy (or scipy)
    into the graph's import path for a few hundred lines of convolution would
    be a poor trade, and the polyphase structure only ever computes the taps it
    actually needs, so the cost is ``output_rate x taps_per_phase`` multiplies
    per second of audio rather than the full upsampled rate.
"""

from __future__ import annotations

import array
import io
import math
import sys
import wave
from math import gcd


# PCM on the wire is always little-endian; `array` is host-endian, so every
# conversion between the two byteswaps on a big-endian host and nowhere else.
_BIG_ENDIAN_HOST = sys.byteorder == "big"


# Windowed-sinc taps PER POLYPHASE BRANCH.  This is the real quality/cost knob:
# the total filter length is ``taps_per_phase * L``, but only ``taps_per_phase``
# multiplies are evaluated per output sample.  16 with a Blackman window puts
# stopband rejection well below the 16-bit noise floor for speech, which is all
# that is being asked of it.
DEFAULT_TAPS_PER_PHASE = 16

# Transition-band placement: the low-pass corner sits at this fraction of the
# lower Nyquist.  0.90 leaves a 10% transition band, so the passband is flat
# through ~3.6 kHz when targeting 8 kHz — comfortably past the top of the
# telephone band — while the stopband is fully developed before anything can
# fold back.
CUTOFF_FRACTION = 0.90

_INT16_MIN = -32768
_INT16_MAX = 32767


def _blackman(index: int, length: int) -> float:
    """Return the Blackman window coefficient at *index* of *length* taps."""
    if length <= 1:
        return 1.0
    ratio = index / (length - 1)
    return (
        0.42
        - 0.5 * math.cos(2.0 * math.pi * ratio)
        + 0.08 * math.cos(4.0 * math.pi * ratio)
    )


def _design_lowpass(length: int, cutoff_normalized: float) -> list[float]:
    """Design a windowed-sinc low-pass FIR with unit DC gain.

    Args:
        length: Total number of taps.
        cutoff_normalized: Corner frequency as a fraction of the sample rate the
            filter runs at (so 0.5 is Nyquist).

    Returns:
        *length* taps summing to 1.0.
    """
    middle = (length - 1) / 2.0
    taps: list[float] = []
    for index in range(length):
        offset = index - middle
        if abs(offset) < 1e-12:
            value = 2.0 * cutoff_normalized
        else:
            angle = 2.0 * math.pi * cutoff_normalized * offset
            value = math.sin(angle) / (math.pi * offset)
        taps.append(value * _blackman(index, length))

    total = sum(taps)
    if total == 0.0:  # pragma: no cover - unreachable for a low-pass design
        return taps
    return [tap / total for tap in taps]


def _clamp_int16(value: float) -> int:
    """Round *value* to the nearest int and clamp it into the int16 range."""
    rounded = int(value + 0.5) if value >= 0 else -int(-value + 0.5)
    if rounded > _INT16_MAX:
        return _INT16_MAX
    if rounded < _INT16_MIN:
        return _INT16_MIN
    return rounded


class PcmResampler:
    """Streaming polyphase resampler for 16-bit signed little-endian mono PCM.

    One instance owns the filter history for exactly one stream.  Construct one
    per turn (or per call) and feed it every chunk in order; feeding two
    interleaved streams through a single instance smears one into the other.

    A source rate equal to the target rate is a supported no-op: the instance
    still carries odd bytes across chunk boundaries, so callers do not need a
    separate code path for "no conversion needed".
    """

    def __init__(
        self,
        *,
        source_rate_hz: int,
        target_rate_hz: int,
        taps_per_phase: int = DEFAULT_TAPS_PER_PHASE,
    ) -> None:
        """Build the polyphase bank for a ``source_rate_hz → target_rate_hz`` conversion.

        Args:
            source_rate_hz: Input sample rate in hertz.
            target_rate_hz: Output sample rate in hertz.
            taps_per_phase: Filter taps evaluated per output sample.

        Raises:
            ValueError: if either rate is not positive, or *taps_per_phase* is
                below 1.
        """
        if source_rate_hz <= 0 or target_rate_hz <= 0:
            raise ValueError(
                "source_rate_hz and target_rate_hz must both be > 0 "
                f"(got {source_rate_hz!r} → {target_rate_hz!r})"
            )
        if taps_per_phase < 1:
            raise ValueError(f"taps_per_phase must be >= 1 (got {taps_per_phase!r})")

        self.source_rate_hz = source_rate_hz
        self.target_rate_hz = target_rate_hz

        divisor = gcd(source_rate_hz, target_rate_hz)
        # Interpolation factor L and decimation factor M, in lowest terms.
        self._interpolate = target_rate_hz // divisor
        self._decimate = source_rate_hz // divisor
        self._taps_per_phase = taps_per_phase
        self._identity = self._interpolate == 1 and self._decimate == 1

        if self._identity:
            self._phases: tuple[tuple[float, ...], ...] = ()
        else:
            self._phases = self._build_phases(taps_per_phase)

        # Filter history: the taps_per_phase-1 most recent input samples, so a
        # chunk boundary is invisible to the convolution.  Primed with silence,
        # which is what precedes the first sample of a call anyway.
        self._history: list[int] = [0] * (taps_per_phase - 1)
        # Absolute output index of the NEXT sample to emit.  Kept across calls
        # because the phase of the polyphase bank is a function of it.
        self._output_index = 0
        # Absolute input index of self._history[0] + len(history) — i.e. how
        # many input samples have been consumed in total.
        self._consumed = 0
        # A trailing byte of an odd-length chunk, held until its pair arrives.
        self._odd_byte = b""

    @property
    def is_identity(self) -> bool:
        """Whether this resampler passes audio through unchanged."""
        return self._identity

    def _build_phases(self, taps_per_phase: int) -> tuple[tuple[float, ...], ...]:
        """Split the prototype low-pass into ``L`` polyphase branches.

        The prototype runs at the upsampled rate ``source * L`` and must cut at
        the lower of the two Nyquist frequencies — the input's when
        downsampling, the output's when upsampling — otherwise either aliasing
        (down) or imaging (up) survives.
        """
        interpolate = self._interpolate
        upsampled_rate = self.source_rate_hz * interpolate
        limit_hz = min(self.source_rate_hz, self.target_rate_hz) / 2.0
        cutoff = CUTOFF_FRACTION * limit_hz / upsampled_rate

        length = taps_per_phase * interpolate
        prototype = _design_lowpass(length, cutoff)

        # Upsampling by L inserts L-1 zeros between samples, dividing the
        # signal's energy by L; the gain is restored here rather than per
        # output sample.
        return tuple(
            tuple(
                prototype[phase + step * interpolate] * interpolate
                for step in range(taps_per_phase)
            )
            for phase in range(interpolate)
        )

    def resample(self, pcm: bytes) -> bytes:
        """Convert one chunk of PCM, carrying filter state into the next call.

        Args:
            pcm: 16-bit signed little-endian mono PCM.  Any length, including
                an odd number of bytes and zero.

        Returns:
            The converted PCM.  May be empty when the chunk was too short to
            advance the output clock — that is normal, not an error.
        """
        if not pcm and not self._odd_byte:
            return b""

        raw = self._odd_byte + pcm
        if len(raw) % 2:
            raw, self._odd_byte = raw[:-1], raw[-1:]
        else:
            self._odd_byte = b""
        if not raw:
            return b""

        if self._identity:
            return raw

        samples = array.array("h")
        samples.frombytes(raw)
        if _BIG_ENDIAN_HOST:  # pragma: no cover - CI and dev are LE
            samples.byteswap()

        return self._convolve(samples)

    def _convolve(self, samples: array.array) -> bytes:
        """Run the polyphase bank over *samples* and return the output PCM."""
        taps = self._taps_per_phase
        interpolate = self._interpolate
        decimate = self._decimate
        phases = self._phases

        # window[i] is the input sample at absolute index (window_origin + i).
        window = self._history + list(samples)
        window_origin = self._consumed - len(self._history)
        last_index = window_origin + len(window) - 1

        out = array.array("h")
        output_index = self._output_index
        while True:
            upsampled_index = output_index * decimate
            base = upsampled_index // interpolate
            if base > last_index:
                break

            phase = phases[upsampled_index % interpolate]
            start = base - window_origin
            total = 0.0
            for step in range(taps):
                position = start - step
                if position < 0:
                    break
                total += phase[step] * window[position]
            out.append(_clamp_int16(total))
            output_index += 1

        self._output_index = output_index
        self._consumed = last_index + 1

        # Retain exactly the history the next call's earliest output can reach.
        keep = taps - 1
        self._history = window[-keep:] if keep else []

        if _BIG_ENDIAN_HOST:  # pragma: no cover - CI and dev are LE
            out.byteswap()
        return out.tobytes()

    def flush(self) -> bytes:
        """Return any output still recoverable once the stream has ended.

        The filter is causal, so there is no buffered look-ahead to drain — the
        only thing that can remain is a trailing odd byte, which is a truncated
        sample and is discarded rather than zero-extended into a click.
        """
        self._odd_byte = b""
        return b""


# ---------------------------------------------------------------------------
# G.711 mu-law companding (ITU-T G.711, the PSTN codec)
# ---------------------------------------------------------------------------

_MULAW_BIAS = 0x84
_MULAW_CLIP = 32635


def encode_mulaw(pcm: bytes) -> bytes:
    """Compand 16-bit linear PCM to 8-bit G.711 mu-law.

    mu-law is what a PSTN leg actually carries, and it halves the byte rate
    on top of the 3x cut from resampling to 8 kHz.  The implementation is the
    standard bias-and-segment form from G.711, not an approximation.

    Args:
        pcm: 16-bit signed little-endian mono PCM.  An odd trailing byte is a
            truncated sample and is dropped.

    Returns:
        One mu-law byte per input sample.
    """
    samples = array.array("h")
    samples.frombytes(pcm[: len(pcm) - (len(pcm) % 2)])
    if _BIG_ENDIAN_HOST:  # pragma: no cover - CI and dev are LE
        samples.byteswap()

    out = bytearray(len(samples))
    for index, sample in enumerate(samples):
        sign = 0x80 if sample < 0 else 0x00
        magnitude = -sample if sample < 0 else sample
        if magnitude > _MULAW_CLIP:
            magnitude = _MULAW_CLIP
        magnitude += _MULAW_BIAS

        exponent = 7
        mask = 0x4000
        while exponent > 0 and not magnitude & mask:
            exponent -= 1
            mask >>= 1

        mantissa = (magnitude >> (exponent + 3)) & 0x0F
        out[index] = ~(sign | (exponent << 4) | mantissa) & 0xFF
    return bytes(out)


def decode_mulaw(data: bytes) -> bytes:
    """Expand 8-bit G.711 mu-law back to 16-bit linear PCM.

    The inverse of :func:`encode_mulaw`, and the reason a mu-law recording can
    still be played back or fed to a recogniser that only speaks linear PCM.

    Args:
        data: One mu-law byte per sample.

    Returns:
        16-bit signed little-endian mono PCM.
    """
    out = array.array("h")
    for byte in data:
        value = ~byte & 0xFF
        sign = value & 0x80
        exponent = (value >> 4) & 0x07
        mantissa = value & 0x0F
        magnitude = ((mantissa << 3) + _MULAW_BIAS) << exponent
        magnitude -= _MULAW_BIAS
        out.append(-magnitude if sign else magnitude)
    if _BIG_ENDIAN_HOST:  # pragma: no cover - CI and dev are LE
        out.byteswap()
    return out.tobytes()


# ---------------------------------------------------------------------------
# WAV containers (dev/recording convenience, not a transport format)
# ---------------------------------------------------------------------------


def pcm16_to_wav(pcm: bytes, *, sample_rate_hz: int) -> bytes:
    """Wrap raw 16-bit mono PCM in a RIFF/WAV container.

    Args:
        pcm: 16-bit signed little-endian mono PCM.
        sample_rate_hz: Sample rate to declare in the header.

    Returns:
        The complete WAV file bytes.
    """
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate_hz)
        handle.writeframes(pcm)
    return buffer.getvalue()


def wav_to_pcm16(data: bytes) -> tuple[bytes, int]:
    """Unwrap a mono 16-bit WAV file into raw PCM and its sample rate.

    Args:
        data: A complete WAV file.

    Returns:
        ``(pcm, sample_rate_hz)``.

    Raises:
        ValueError: if the file is not 16-bit mono.
    """
    with wave.open(io.BytesIO(data), "rb") as handle:
        if handle.getnchannels() != 1 or handle.getsampwidth() != 2:
            raise ValueError(
                "expected 16-bit mono WAV, got "
                f"{handle.getnchannels()} channel(s) at "
                f"{handle.getsampwidth() * 8}-bit"
            )
        return handle.readframes(handle.getnframes()), handle.getframerate()
