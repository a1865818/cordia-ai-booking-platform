"""Core state models for the Cordia hello graph.

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, Field


class GreetingStatus(str, Enum):
    """Lifecycle status of a greeting."""

    PENDING = "pending"
    GREETED = "greeted"


class StreamFrameType(str, Enum):
    """Discriminator for streaming voice-pipeline frames (spec 01 §5.1 / §6.3)."""

    AUDIO_STARTED = "audio.started"
    TRANSCRIPT_PARTIAL = "transcript.partial"
    TRANSCRIPT_FINAL = "transcript.final"
    AGENT_TEXT_DELTA = "agent.text.delta"
    AGENT_TEXT_FINAL = "agent.text.final"
    TOOL_PROGRESS = "tool.progress"
    TTS_AUDIO_DELTA = "tts.audio.delta"
    BARGE_IN_DETECTED = "barge_in.detected"
    TURN_CANCELLED = "turn.cancelled"


class HelloInputState(BaseModel):
    """Input to the hello graph."""

    model_config = {"extra": "forbid"}

    name: str = Field(description="Name of the person to greet.")


class HelloOutputState(BaseModel):
    """Output of the hello graph."""

    model_config = {"extra": "forbid"}

    message: str = Field(description="The rendered greeting message.")
    status: GreetingStatus = Field(
        default=GreetingStatus.GREETED, description="Resulting greeting status."
    )
