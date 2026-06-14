"""Streaming message models emitted by the Cordia hello graph."""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, Field


class HelloMessageCode(str, Enum):
    """Discriminator for hello-graph stream messages."""

    STARTED = "started"
    COMPLETED = "completed"


class HelloStreamMessage(BaseModel):
    """A single streamed message frame from the hello graph."""

    model_config = {"extra": "forbid"}

    code: HelloMessageCode = Field(description="Message code discriminator.")
    text: str = Field(description="Human-readable message text.")
