"""Streaming voice-pipeline frame models (spec 01 §5.1 / §6.3).

Source-of-truth Pydantic v2 models for the streaming frame contract. Every
frame carries the shared :class:`StreamFrameEnvelope` identity/ordering fields
and a per-variant payload. These are a pure data contract — no behavior.
"""

from __future__ import annotations

from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field

from cordia_agent.state import StreamFrameType


class StreamFrameEnvelope(BaseModel):
    """Shared identity/ordering envelope carried by every stream frame."""

    model_config = {"extra": "forbid"}

    type: StreamFrameType = Field(description="Frame type discriminator.")
    tenant_id: str = Field(
        description="Opaque tenant scope, propagated not resolved (Inv-13)."
    )
    session_id: str = Field(description="Identifies one call/conversation.")
    turn_id: str = Field(description="Identifies one customer/agent contribution.")
    correlation_id: str = Field(
        description="Correlation id, begins at channel ingress (§25.1)."
    )
    causation_id: str | None = Field(
        default=None, description="Event-lineage parent id."
    )
    seq: int = Field(ge=0, description="Monotonic per-session ordering counter.")
    ts: AwareDatetime = Field(description="ISO-8601 timestamp with offset.")


class AudioStartedFrame(StreamFrameEnvelope):
    """Audio capture started; no payload beyond the envelope."""

    type: Literal[StreamFrameType.AUDIO_STARTED] = Field(
        description="Frame type discriminator."
    )


class TranscriptPartialFrame(StreamFrameEnvelope):
    """Interim transcript for display/barge-in only (Inv-1: never committable)."""

    type: Literal[StreamFrameType.TRANSCRIPT_PARTIAL] = Field(
        description="Frame type discriminator.",
    )
    text: str = Field(description="Interim recognized transcript text.")
    stability: float = Field(description="Provider stability hint (unbounded).")


class TranscriptFinalFrame(StreamFrameEnvelope):
    """Committable final transcript (Inv-1: only finals may mutate booking state)."""

    type: Literal[StreamFrameType.TRANSCRIPT_FINAL] = Field(
        description="Frame type discriminator.",
    )
    text: str = Field(description="Final recognized transcript text.")
    asr_confidence: float = Field(ge=0.0, le=1.0, description="ASR confidence, 0..1.")
    is_endpoint: bool = Field(
        default=True, description="Whether this marks an utterance endpoint."
    )


class AgentTextDeltaFrame(StreamFrameEnvelope):
    """Incremental agent response text chunk."""

    type: Literal[StreamFrameType.AGENT_TEXT_DELTA] = Field(
        description="Frame type discriminator.",
    )
    delta: str = Field(description="Incremental agent response text chunk.")


class AgentTextFinalFrame(StreamFrameEnvelope):
    """Final, complete agent response text."""

    type: Literal[StreamFrameType.AGENT_TEXT_FINAL] = Field(
        description="Frame type discriminator.",
    )
    text: str = Field(description="Final, complete agent response text.")


class ToolProgressFrame(StreamFrameEnvelope):
    """Progress signal for an in-flight tool invocation."""

    type: Literal[StreamFrameType.TOOL_PROGRESS] = Field(
        description="Frame type discriminator."
    )
    tool_name: str = Field(description="Name of the tool being invoked.")
    phase: str = Field(description='Progress phase, e.g. "checking_availability".')
    elapsed_ms: int = Field(
        ge=0, description="Elapsed time since invocation, in milliseconds."
    )


class TtsAudioDeltaFrame(StreamFrameEnvelope):
    """Reference to a synthesized audio chunk (bytes travel on the media plane)."""

    type: Literal[StreamFrameType.TTS_AUDIO_DELTA] = Field(
        description="Frame type discriminator."
    )
    audio_chunk_ref: str = Field(
        description="Opaque chunk id; raw bytes are out of band on the media plane."
    )
    chunk_seq: int = Field(
        ge=0, description="Monotonic ordering counter for audio chunks."
    )


class BargeInFrame(StreamFrameEnvelope):
    """Caller barge-in detected; no payload beyond the envelope."""

    type: Literal[StreamFrameType.BARGE_IN_DETECTED] = Field(
        description="Frame type discriminator.",
    )


class TurnCancelledFrame(StreamFrameEnvelope):
    """Turn cancelled; gates the Inv-16 non-authoritative rule."""

    type: Literal[StreamFrameType.TURN_CANCELLED] = Field(
        description="Frame type discriminator."
    )
    cancelled_turn_id: str = Field(description="Identifier of the cancelled turn.")
    material_content_delivered: bool = Field(
        description=(
            "Whether material content was delivered before cancellation "
            "(Inv-16: false => interrupted speech is non-authoritative)."
        )
    )
