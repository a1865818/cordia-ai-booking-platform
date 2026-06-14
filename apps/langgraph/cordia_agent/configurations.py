"""Runtime configuration models for the Cordia hello graph."""

from __future__ import annotations

from pydantic import BaseModel, Field


class HelloConfiguration(BaseModel):
    """Configurable parameters for the hello graph."""

    model_config = {"extra": "forbid"}

    greeting: str = Field(default="Hello", description="Greeting prefix to use.")
