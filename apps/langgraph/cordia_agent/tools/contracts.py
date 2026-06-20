"""Tool-contract envelope for the agent<->platform boundary (spec 03 §4.2).

This Pydantic model is the source of truth for the shared types exported to
C# and TypeScript/Zod via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


SCHEMA_VERSION = "1.0.0"


class ToolResultEnvelope(BaseModel):
    """Single closed wrapper returned by every tool (spec 03 §4.2)."""

    model_config = {"extra": "forbid"}

    ok: bool = Field(description="Machine-readable success/failure.")
    status: str = Field(description="Per-tool status code; never raw error text.")
    customer_hint: str | None = Field(
        default=None,
        description="Safe customer-facing string; never raw provider text.",
    )
    recoverable_fields: list[str] = Field(
        default_factory=list, description="Fields the agent may re-prompt to correct."
    )
    suggested_action: str | None = Field(
        default=None,
        description="Machine hint for next move, e.g. request_alternative_time.",
    )
    data: dict | None = Field(
        default=None, description="Tool-specific typed payload (per-tool Out schemas)."
    )
    correlation_id: str = Field(
        description="Propagated from the typed tool client; consumed by observability."
    )
    schema_version: str = Field(description="Cross-language contract version.")
