"""Unit tests for the energy-plus-hangover voice activity detector."""

from __future__ import annotations

import struct

import pytest

from cordia_agent.streaming.providers.vad import (
    EnergyVoiceActivityDetector,
    VadEvent,
    frame_rms,
)


FRAME_BYTES = 640  # 16 kHz, 20 ms, 16-bit mono


def pcm_frame(amplitude: int, *, frame_bytes: int = FRAME_BYTES) -> bytes:
    """Build one PCM frame whose every sample has |value| == *amplitude*."""
    sample_count = frame_bytes // 2
    return struct.pack(f"<{sample_count}h", *([amplitude] * sample_count))


def _detector(**overrides: object) -> EnergyVoiceActivityDetector:
    kwargs: dict[str, object] = {
        "sample_rate_hz": 16000,
        "frame_ms": 20,
        "energy_threshold": 300.0,
        "onset_frames": 2,
        "hangover_frames": 3,
    }
    kwargs.update(overrides)
    return EnergyVoiceActivityDetector(**kwargs)  # type: ignore[arg-type]


@pytest.mark.unit
def test_frame_rms_of_constant_amplitude_equals_that_amplitude() -> None:
    assert frame_rms(pcm_frame(1000)) == pytest.approx(1000.0)
    assert frame_rms(pcm_frame(0)) == 0.0
    assert frame_rms(b"") == 0.0


@pytest.mark.unit
def test_frame_bytes_is_derived_from_rate_and_frame_duration() -> None:
    assert _detector().frame_bytes == FRAME_BYTES
    assert _detector(sample_rate_hz=8000).frame_bytes == 320


@pytest.mark.unit
def test_silence_never_starts_an_utterance() -> None:
    detector = _detector()

    events = [detector.process_frame(pcm_frame(10)) for _ in range(20)]

    assert set(events) == {VadEvent.NONE}
    assert detector.in_speech is False


@pytest.mark.unit
def test_single_loud_frame_is_debounced_by_onset_frames() -> None:
    detector = _detector(onset_frames=2)

    assert detector.process_frame(pcm_frame(5000)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.in_speech is False


@pytest.mark.unit
def test_speech_start_fires_on_the_onset_frame_only() -> None:
    detector = _detector(onset_frames=2)

    assert detector.process_frame(pcm_frame(5000)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(5000)) is VadEvent.SPEECH_START
    assert detector.process_frame(pcm_frame(5000)) is VadEvent.NONE
    assert detector.in_speech is True


@pytest.mark.unit
def test_speech_end_requires_a_full_hangover_of_silence() -> None:
    detector = _detector(onset_frames=1, hangover_frames=3)

    assert detector.process_frame(pcm_frame(5000)) is VadEvent.SPEECH_START
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.SPEECH_END
    assert detector.in_speech is False


@pytest.mark.unit
def test_mid_utterance_pause_shorter_than_hangover_does_not_endpoint() -> None:
    detector = _detector(onset_frames=1, hangover_frames=3)
    detector.process_frame(pcm_frame(5000))

    # Two silent frames then speech again — a natural mid-sentence pause.
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(5000)) is VadEvent.NONE
    assert detector.in_speech is True

    # The silence run restarted, so it takes another full hangover to end.
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(10)) is VadEvent.SPEECH_END


@pytest.mark.unit
def test_threshold_is_inclusive_at_the_boundary() -> None:
    detector = _detector(onset_frames=1, energy_threshold=300.0)

    assert detector.process_frame(pcm_frame(299)) is VadEvent.NONE
    assert detector.process_frame(pcm_frame(300)) is VadEvent.SPEECH_START


@pytest.mark.unit
def test_reset_returns_the_detector_to_its_initial_state() -> None:
    detector = _detector(onset_frames=1)
    detector.process_frame(pcm_frame(5000))
    assert detector.in_speech is True

    detector.reset()

    assert detector.in_speech is False
    assert detector.process_frame(pcm_frame(5000)) is VadEvent.SPEECH_START


@pytest.mark.unit
def test_wrong_frame_size_is_rejected() -> None:
    detector = _detector()

    with pytest.raises(ValueError, match="exactly 640 bytes"):
        detector.process_frame(pcm_frame(1000, frame_bytes=320))


@pytest.mark.unit
@pytest.mark.parametrize(
    "override",
    [
        {"sample_rate_hz": 0},
        {"frame_ms": 0},
        {"energy_threshold": 0.0},
        {"onset_frames": 0},
        {"hangover_frames": 0},
    ],
)
def test_non_positive_construction_arguments_are_rejected(
    override: dict[str, object],
) -> None:
    with pytest.raises(ValueError, match="must be > 0"):
        _detector(**override)


@pytest.mark.unit
def test_frame_duration_must_divide_into_whole_samples() -> None:
    with pytest.raises(ValueError, match="whole samples"):
        _detector(sample_rate_hz=44100, frame_ms=1)
