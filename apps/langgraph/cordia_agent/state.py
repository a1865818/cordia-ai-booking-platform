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
