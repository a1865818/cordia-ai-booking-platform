"""Correlation-context header model — source of truth for C#/TS shared type generation.

Spec refs: §25.1 (correlation ID minting and lineage), §4.1 (cross-service header set).
Every cross-service contract carries these fields; they are threaded through tool calls,
graph nodes, the outbox/inbox/broker, and the .NET MediatR pipeline without mutation
(the same ``correlation_id`` survives the full flow; ``causation_id`` is advanced per hop).

This model is registered in ``tools/scripts/export_pydantic_schemas.py`` so that the
C# ``Cordia.Domain.Generated.CorrelationContext`` record and the TypeScript interface +
Zod schema are generated automatically by the shared-schemas pipeline.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


class CorrelationContext(BaseModel):
    """Cross-service correlation envelope (spec 13 §4.1 / §25.1).

    Closed schema (``extra="forbid"``) so the generated Zod schema uses ``.strict()``
    and the C# record rejects unknown properties.  Not frozen — the context is
    carried and updated across hops (e.g. ``causation_id`` advances on each hop).
    """

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(
        description=(
            "Stable tenant identifier. Set before any data access (Inv-13). "
            "Derived solely from the verified TenantContext; never supplied raw by callers."
        )
    )
    correlation_id: str = Field(
        description=(
            "Minted once at channel ingress and threaded unchanged through every "
            "downstream span, tool call, and queue envelope (§25.1). "
            "Never re-minted mid-flow."
        )
    )
    causation_id: str | None = Field(
        default=None,
        description=(
            "The ID of the event, span, or message that directly caused this unit of work. "
            "Advanced per hop to preserve lineage (§25.1). "
            "Absent only at the very first ingress point."
        ),
    )
    session_id: str | None = Field(
        default=None,
        description=(
            "Foreground session identifier (specs 01/02 only). "
            "Not present on background/durable-operation contexts."
        ),
    )
    turn_id: str | None = Field(
        default=None,
        description=(
            "Foreground per-voice-turn identifier (spec 01). "
            "Not present on background/durable-operation contexts."
        ),
    )
    operation_id: str | None = Field(
        default=None,
        description=(
            "Durable operation identifier. Present once a confirmed-intent operation "
            "has been accepted (spec 04+). Absent during pure foreground turns."
        ),
    )
