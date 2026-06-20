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

# The fixed `type` discriminator member each variant pins itself to.
VARIANT_TYPE: dict[type[BaseModel], StreamFrameType] = {
    AudioStartedFrame: StreamFrameType.AUDIO_STARTED,
    TranscriptPartialFrame: StreamFrameType.TRANSCRIPT_PARTIAL,
    TranscriptFinalFrame: StreamFrameType.TRANSCRIPT_FINAL,
    AgentTextDeltaFrame: StreamFrameType.AGENT_TEXT_DELTA,
    AgentTextFinalFrame: StreamFrameType.AGENT_TEXT_FINAL,
    ToolProgressFrame: StreamFrameType.TOOL_PROGRESS,
    TtsAudioDeltaFrame: StreamFrameType.TTS_AUDIO_DELTA,
    BargeInFrame: StreamFrameType.BARGE_IN_DETECTED,
    TurnCancelledFrame: StreamFrameType.TURN_CANCELLED,
}

# Per-variant payload fields required beyond the envelope + `type` discriminator.
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


def _valid_fields(variant: type[BaseModel]) -> dict[str, object]:
    """Full set of valid construction kwargs for a variant (envelope + type + payload)."""
    return {
        "type": VARIANT_TYPE[variant],
        **ENVELOPE_FIELDS,
        **VARIANT_PAYLOADS[variant],
    }


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
        variant(**_valid_fields(variant), bogus="x")


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_round_trip(variant: type[BaseModel]) -> None:
    """AC4: model_validate(model_dump()) round-trips for every variant."""
    frame = variant(**_valid_fields(variant))
    assert variant.model_validate(frame.model_dump()) == frame


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_variant_type_is_fixed_discriminator(variant: type[BaseModel]) -> None:
    """AC1/AC3: each variant pins `type` to its own StreamFrameType member."""
    frame = variant(**_valid_fields(variant))
    assert frame.type == VARIANT_TYPE[variant]


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_type_discriminator_is_required(variant: type[BaseModel]) -> None:
    """The `type` discriminator has no default and must be supplied explicitly."""
    fields = {**ENVELOPE_FIELDS, **VARIANT_PAYLOADS[variant]}
    with pytest.raises(ValidationError):
        variant(**fields)


@pytest.mark.unit
@pytest.mark.parametrize("variant", ALL_VARIANTS)
def test_wrong_type_value_rejected(variant: type[BaseModel]) -> None:
    """A `type` value other than the variant's own member is rejected (single-value const)."""
    wrong = next(m for m in StreamFrameType if m is not VARIANT_TYPE[variant])
    fields = {**_valid_fields(variant), "type": wrong}
    with pytest.raises(ValidationError):
        variant(**fields)


@pytest.mark.unit
@pytest.mark.parametrize("confidence", [0.0, 0.5, 1.0])
def test_asr_confidence_accepts_in_range(confidence: float) -> None:
    """AC3: asr_confidence accepts values in 0..1."""
    frame = TranscriptFinalFrame(
        type=StreamFrameType.TRANSCRIPT_FINAL,
        **ENVELOPE_FIELDS,
        text="hi",
        asr_confidence=confidence,
    )
    assert frame.asr_confidence == confidence


@pytest.mark.unit
@pytest.mark.parametrize("confidence", [-0.1, 1.1])
def test_asr_confidence_rejects_out_of_range(confidence: float) -> None:
    """AC3: asr_confidence out of 0..1 raises ValidationError."""
    with pytest.raises(ValidationError):
        TranscriptFinalFrame(
            type=StreamFrameType.TRANSCRIPT_FINAL,
            **ENVELOPE_FIELDS,
            text="hi",
            asr_confidence=confidence,
        )


@pytest.mark.unit
def test_seq_rejects_negative() -> None:
    """seq is a non-negative monotonic counter (ge=0)."""
    with pytest.raises(ValidationError):
        AudioStartedFrame(
            type=StreamFrameType.AUDIO_STARTED, **{**ENVELOPE_FIELDS, "seq": -1}
        )


@pytest.mark.unit
def test_seq_accepts_zero() -> None:
    """seq accepts the lower bound of zero."""
    frame = AudioStartedFrame(
        type=StreamFrameType.AUDIO_STARTED, **{**ENVELOPE_FIELDS, "seq": 0}
    )
    assert frame.seq == 0


@pytest.mark.unit
def test_elapsed_ms_rejects_negative() -> None:
    """elapsed_ms is non-negative elapsed time (ge=0)."""
    fields = {**_valid_fields(ToolProgressFrame), "elapsed_ms": -1}
    with pytest.raises(ValidationError):
        ToolProgressFrame(**fields)


@pytest.mark.unit
def test_chunk_seq_rejects_negative() -> None:
    """chunk_seq is a non-negative monotonic counter (ge=0)."""
    fields = {**_valid_fields(TtsAudioDeltaFrame), "chunk_seq": -1}
    with pytest.raises(ValidationError):
        TtsAudioDeltaFrame(**fields)


@pytest.mark.unit
def test_turn_cancelled_requires_payload() -> None:
    """AC3: TurnCancelledFrame requires cancelled_turn_id + material_content_delivered."""
    with pytest.raises(ValidationError):
        TurnCancelledFrame(type=StreamFrameType.TURN_CANCELLED, **ENVELOPE_FIELDS)


@pytest.mark.unit
def test_tool_progress_requires_payload() -> None:
    """AC3: ToolProgressFrame requires tool_name, phase, and elapsed_ms."""
    with pytest.raises(ValidationError):
        ToolProgressFrame(type=StreamFrameType.TOOL_PROGRESS, **ENVELOPE_FIELDS)


@pytest.mark.unit
def test_tts_audio_delta_requires_payload() -> None:
    """AC3: TtsAudioDeltaFrame requires audio_chunk_ref + chunk_seq."""
    with pytest.raises(ValidationError):
        TtsAudioDeltaFrame(type=StreamFrameType.TTS_AUDIO_DELTA, **ENVELOPE_FIELDS)


@pytest.mark.unit
@pytest.mark.parametrize("variant", [AudioStartedFrame, BargeInFrame])
def test_no_payload_variants_need_only_envelope_and_type(
    variant: type[BaseModel],
) -> None:
    """AC3: AudioStartedFrame/BargeInFrame validate with envelope + type only."""
    frame = variant(type=VARIANT_TYPE[variant], **ENVELOPE_FIELDS)
    assert frame.type == VARIANT_TYPE[variant]


@pytest.mark.unit
def test_partial_rejects_committable_only_fields() -> None:
    """AC5: TranscriptPartialFrame rejects committable-only fields."""
    with pytest.raises(ValidationError):
        TranscriptPartialFrame(
            type=StreamFrameType.TRANSCRIPT_PARTIAL,
            **ENVELOPE_FIELDS,
            text="hi",
            stability=0.4,
            asr_confidence=0.9,
        )
    with pytest.raises(ValidationError):
        TranscriptPartialFrame(
            type=StreamFrameType.TRANSCRIPT_PARTIAL,
            **ENVELOPE_FIELDS,
            text="hi",
            stability=0.4,
            is_endpoint=True,
        )


@pytest.mark.unit
def test_final_rejects_partial_only_fields() -> None:
    """AC5: TranscriptFinalFrame rejects partial-only fields (stability)."""
    with pytest.raises(ValidationError):
        TranscriptFinalFrame(
            type=StreamFrameType.TRANSCRIPT_FINAL,
            **ENVELOPE_FIELDS,
            text="hi",
            asr_confidence=0.9,
            stability=0.4,
        )
