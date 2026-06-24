"""HandoffPackage model for request_human_handoff (spec 02; Inv-11)."""

from __future__ import annotations

from pydantic import BaseModel, Field

from cordia_agent.state import BookingDraft


class HandoffPackage(BaseModel):
    """Structured payload for request_human_handoff (spec 02; Inv-11 packager input)."""

    model_config = {"extra": "forbid"}

    reason: str = Field(description="Why the conversation is being handed to a human.")
    draft_snapshot: BookingDraft | None = Field(
        default=None,
        description="Draft state at handoff (provenance retained internally).",
    )
    source_turn_ids: list[str] = Field(
        default_factory=list,
        description="Turn IDs pointing to the relevant transcript context.",
    )
    summary: str | None = Field(
        default=None,
        description="Optional human-readable summary for the agent picking up.",
    )
