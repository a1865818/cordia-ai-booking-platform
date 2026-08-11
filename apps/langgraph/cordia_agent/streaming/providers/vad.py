"""Voice-activity detection port and its dependency-free default.

SCOPE: this module exists solely for the BATCH STT path.  Its only caller is
``azure_stt.AzureWhisperSTTProvider``.  The default provider,
``azure_realtime_stt.AzureRealtimeSTTProvider``, gets endpointing and speech
onset from the service's own ``server_vad`` and never constructs a detector —
so this code runs only on a resource configured for the ``whisper`` batch
deployment.  Keep the two in step: a change to endpointing feel here has no
effect on the realtime path, where the equivalent knob is
``realtime_silence_duration_ms``.

Whisper is a BATCH recogniser: it needs a complete utterance, not a stream.
Something therefore has to decide where an utterance starts and ends, and that
something is this module.  It is a port rather than a hard-coded helper for two
reasons: endpointing quality is the dominant contributor to perceived latency
and will be tuned, and a better detector (webrtcvad, Silero) may be dropped in
later without touching the STT provider.

The shipped default is an energy-plus-hangover detector implemented on the
stdlib alone.  That is a deliberate trade:

- It adds no dependency.  A neural VAD pulls in torch or a native wheel, which
  is a large cost for a component whose output is a single boolean per 20 ms.
- Its failure mode is understandable and testable offline: it triggers on loud
  non-speech and misses very quiet speech.  A neural VAD's failure mode is not.
- Hangover (a run of silence frames required before declaring the end) is what
  actually determines endpointing feel, and it is detector-independent.

Frames are fixed-size 16-bit signed little-endian mono PCM.  Re-framing the
arbitrary-sized inbound chunks into whole frames is the caller's job — see
``AzureWhisperSTTProvider``.
"""

from __future__ import annotations

import sys
from array import array
from enum import StrEnum
from typing import Protocol


class VadEvent(StrEnum):
    """Transition reported for one analysed frame.

    Only transitions are reported; steady-state speech and steady-state silence
    both report :attr:`NONE`.  Callers track "am I inside an utterance" from the
    transitions, which keeps the port free of any buffering opinion.
    """

    NONE = "none"
    SPEECH_START = "speech_start"
    SPEECH_END = "speech_end"


class VoiceActivityDetector(Protocol):
    """Port deciding where caller utterances begin and end.

    Implementations are stateful and single-stream: one instance analyses one
    audio stream at a time, and :meth:`reset` returns it to its initial state.
    """

    @property
    def frame_bytes(self) -> int:
        """Exact byte length every frame passed to :meth:`process_frame` must have."""
        ...

    def process_frame(self, frame: bytes) -> VadEvent:
        """Analyse one frame and report any utterance transition.

        Args:
            frame: Exactly :attr:`frame_bytes` of 16-bit signed little-endian
                mono PCM.

        Returns:
            The transition this frame caused, or :attr:`VadEvent.NONE`.

        Raises:
            ValueError: if *frame* is not exactly :attr:`frame_bytes` long.
        """
        ...

    def reset(self) -> None:
        """Return the detector to its initial, not-in-speech state."""
        ...


def frame_rms(frame: bytes) -> float:
    """Return the RMS amplitude of a 16-bit signed little-endian PCM frame.

    Byte order is normalised explicitly rather than trusting ``array``'s native
    order, so the same audio scores identically on a big-endian host.

    Args:
        frame: PCM bytes.  An odd trailing byte is ignored.

    Returns:
        RMS amplitude on the int16 scale (0.0 .. 32768.0).  An empty frame
        scores 0.0.
    """
    usable = len(frame) - (len(frame) % 2)
    if usable == 0:
        return 0.0

    samples = array("h")
    samples.frombytes(frame[:usable])
    if sys.byteorder == "big":
        samples.byteswap()

    total = 0
    for sample in samples:
        total += sample * sample
    return (total / len(samples)) ** 0.5


class EnergyVoiceActivityDetector:
    """Energy-threshold VAD with onset debouncing and silence hangover.

    Speech starts once *onset_frames* consecutive frames exceed the energy
    threshold, which suppresses single-frame clicks and line pops.  Speech ends
    once *hangover_frames* consecutive frames fall below it, which is what lets
    a caller pause mid-sentence without being endpointed.

    The threshold is absolute (no adaptive noise floor).  On a noisy line this
    over-triggers; the fix is a better detector behind the same port, not a
    more elaborate heuristic here.
    """

    def __init__(
        self,
        *,
        sample_rate_hz: int = 16000,
        frame_ms: int = 20,
        energy_threshold: float = 300.0,
        onset_frames: int = 2,
        hangover_frames: int = 25,
    ) -> None:
        """Initialise the detector.

        Args:
            sample_rate_hz: Sample rate of the inbound PCM.
            frame_ms: Analysis frame duration in milliseconds.
            energy_threshold: RMS amplitude (int16 scale) at or above which a
                frame counts as speech.
            onset_frames: Consecutive speech frames required to start.
            hangover_frames: Consecutive silence frames required to end.

        Raises:
            ValueError: if any argument is non-positive, or if *frame_ms* does
                not divide into whole samples at *sample_rate_hz*.
        """
        for name, value in (
            ("sample_rate_hz", sample_rate_hz),
            ("frame_ms", frame_ms),
            ("energy_threshold", energy_threshold),
            ("onset_frames", onset_frames),
            ("hangover_frames", hangover_frames),
        ):
            if value <= 0:
                raise ValueError(f"{name} must be > 0 (got {value!r})")
        if (sample_rate_hz * frame_ms) % 1000 != 0:
            raise ValueError(
                f"frame_ms={frame_ms} does not divide into whole samples at "
                f"sample_rate_hz={sample_rate_hz}"
            )

        self._frame_bytes = (sample_rate_hz * frame_ms // 1000) * 2
        self._energy_threshold = energy_threshold
        self._onset_frames = onset_frames
        self._hangover_frames = hangover_frames
        self._in_speech = False
        self._speech_run = 0
        self._silence_run = 0

    @property
    def frame_bytes(self) -> int:
        """Exact byte length every analysed frame must have."""
        return self._frame_bytes

    @property
    def in_speech(self) -> bool:
        """Whether the detector currently considers itself inside an utterance."""
        return self._in_speech

    def reset(self) -> None:
        """Return the detector to its initial, not-in-speech state."""
        self._in_speech = False
        self._speech_run = 0
        self._silence_run = 0

    def process_frame(self, frame: bytes) -> VadEvent:
        """Analyse one frame and report any utterance transition.

        Args:
            frame: Exactly :attr:`frame_bytes` of 16-bit signed little-endian
                mono PCM.

        Returns:
            The transition this frame caused, or :attr:`VadEvent.NONE`.

        Raises:
            ValueError: if *frame* is not exactly :attr:`frame_bytes` long.
        """
        if len(frame) != self._frame_bytes:
            raise ValueError(
                f"frame must be exactly {self._frame_bytes} bytes, got {len(frame)}"
            )

        loud = frame_rms(frame) >= self._energy_threshold

        if not self._in_speech:
            self._speech_run = self._speech_run + 1 if loud else 0
            if self._speech_run >= self._onset_frames:
                self._in_speech = True
                self._speech_run = 0
                self._silence_run = 0
                return VadEvent.SPEECH_START
            return VadEvent.NONE

        self._silence_run = 0 if loud else self._silence_run + 1
        if self._silence_run >= self._hangover_frames:
            self._in_speech = False
            self._speech_run = 0
            self._silence_run = 0
            return VadEvent.SPEECH_END
        return VadEvent.NONE
