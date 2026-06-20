"""Unit tests for the streaming voice-pipeline frame models."""

import pytest
from pydantic import BaseModel, ValidationError

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    AudioStartedFrame,
    BargeInFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)

# Minimal valid envelope fields shared across constructions below.
ENVELOPE_FIELDS = {
    "tenant_id": "t",
    "session_id": "s",
    "turn_id": "u",
    "correlation_id": "c",
    "seq": 1,
    "ts": "2026-06-18T00:00:00+00:00",
}

# Per-variant payload fields required (beyond the envelope) to construct each frame.
VARIANT_PAYLOADS: dict[type[BaseModel], dict[str, object]] = {
    AudioStartedFrame: {},
    TranscriptPartialFrame: {"text": "hi", "stability": 0.4},
    TranscriptFinalFrame: {"text": "hi", "asr_confidence": 0.9},
    AgentTextDeltaFrame: {"delta": "he"},
    AgentTextFinalFrame: {"text": "hello there"},
    ToolProgressFrame: {
        "tool_name": "availability",
        "phase": "checking_availability",
        "elapsed_ms": 120,
    },
    TtsAudioDeltaFrame: {"audio_chunk_ref": "chunk_1", "chunk_seq": 0},
    BargeInFrame: {},
    TurnCancelledFrame: {
        "cancelled_turn_id": "u",
        "material_content_delivered": False,
    },
}

ALL_VARIANTS = list(VARIANT_PAYLOADS)


@pytest.mark.unit
@pytest.mark.parametrize(
    ("member", "value"),
    [
        (StreamFrameType.AUDIO_STARTED, "audio.started"),
        (StreamFrameType.TRANSCRIPT_PARTIAL, "transcript.partial"),
        (StreamFrameType.TRANSCRIPT_FINAL, "transcript.final"),
        (StreamFrameType.AGENT_TEXT_DELTA, "agent.text.delta"),
        (StreamFrameType.AGENT_TEXT_FINAL, "agent.text.final"),
        (StreamFrameType.TOOL_PROGRESS, "tool.progress"),
        (StreamFrameType.TTS_AUDIO_DELTA, "tts.audio.delta"),
        (StreamFrameType.BARGE_IN_DETECTED, "barge_in.detected"),
        (StreamFrameType.TURN_CANCELLED, "turn.cancelled"),
    ],
)
def test_stream_frame_type_values(member: StreamFrameType, value: str) -> None:
    """AC1: the 9 enum values match the spec verbatim."""
    assert member.value == value


@pytest.mark.unit
def test_stream_frame_type_has_exactly_nine_members() -> None:
    """AC1: the enum carries exactly the 9 spec members, no more, no fewer."""
    assert len(StreamFrameType) == 9


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_extra_field_forbidden(variant: type[BaseModel]) -> None:
    """AC2: unknown fields raise ValidationError (extra='forbid' inherited)."""
    with pytest.raises(ValidationError):
        variant(**ENVELOPE_FIELDS, **VARIANT_PAYLOADS[variant], bogus="x")


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_round_trip(variant: type[BaseModel]) -> None:
    """AC4: model_validate(model_dump()) round-trips for every variant."""
    frame = variant(**ENVELOPE_FIELDS, **VARIANT_PAYLOADS[variant])
    assert variant.model_validate(frame.model_dump()) == frame


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_variant_default_type(variant: type[BaseModel]) -> None:
    """AC1/AC3: each variant defaults `type` to its own StreamFrameType member."""
    frame = variant(**ENVELOPE_FIELDS, **VARIANT_PAYLOADS[variant])
    assert isinstance(frame.type, StreamFrameType)


@pytest.mark.unit
@pytest.mark.parametrize("confidence", [0.0, 0.5, 1.0])
def test_asr_confidence_accepts_in_range(confidence: float) -> None:
    """AC3: asr_confidence accepts values in 0..1."""
    frame = TranscriptFinalFrame(
        **ENVELOPE_FIELDS, text="hi", asr_confidence=confidence
    )
    assert frame.asr_confidence == confidence


@pytest.mark.unit
@pytest.mark.parametrize("confidence", [-0.1, 1.1])
def test_asr_confidence_rejects_out_of_range(confidence: float) -> None:
    """AC3: asr_confidence out of 0..1 raises ValidationError."""
    with pytest.raises(ValidationError):
        TranscriptFinalFrame(
            **ENVELOPE_FIELDS, text="hi", asr_confidence=confidence
        )


@pytest.mark.unit
def test_turn_cancelled_requires_payload() -> None:
    """AC3: TurnCancelledFrame requires cancelled_turn_id + material_content_delivered."""
    with pytest.raises(ValidationError):
        TurnCancelledFrame(**ENVELOPE_FIELDS)


@pytest.mark.unit
def test_tool_progress_requires_payload() -> None:
    """AC3: ToolProgressFrame requires tool_name, phase, and elapsed_ms."""
    with pytest.raises(ValidationError):
        ToolProgressFrame(**ENVELOPE_FIELDS)


@pytest.mark.unit
def test_tts_audio_delta_requires_payload() -> None:
    """AC3: TtsAudioDeltaFrame requires audio_chunk_ref + chunk_seq."""
    with pytest.raises(ValidationError):
        TtsAudioDeltaFrame(**ENVELOPE_FIELDS)


@pytest.mark.unit
@pytest.mark.parametrize("variant", [AudioStartedFrame, BargeInFrame])
def test_no_payload_variants_validate_with_envelope_only(
    variant: type[BaseModel],
) -> None:
    """AC3: AudioStartedFrame/BargeInFrame validate with envelope fields only."""
    frame = variant(**ENVELOPE_FIELDS)
    assert isinstance(frame.type, StreamFrameType)


@pytest.mark.unit
def test_partial_rejects_committable_only_fields() -> None:
    """AC5: TranscriptPartialFrame rejects committable-only fields."""
    with pytest.raises(ValidationError):
        TranscriptPartialFrame(
            **ENVELOPE_FIELDS, text="hi", stability=0.4, asr_confidence=0.9
        )
    with pytest.raises(ValidationError):
        TranscriptPartialFrame(
            **ENVELOPE_FIELDS, text="hi", stability=0.4, is_endpoint=True
        )


@pytest.mark.unit
def test_final_rejects_partial_only_fields() -> None:
    """AC5: TranscriptFinalFrame rejects partial-only fields (stability)."""
    with pytest.raises(ValidationError):
        TranscriptFinalFrame(
            **ENVELOPE_FIELDS, text="hi", asr_confidence=0.9, stability=0.4
        )
