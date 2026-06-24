"""Node: resolve_tenant — §8.1 Tenant Resolution.

Validates that the incoming tenant_id is known and loads the basic tenant
record so subsequent nodes can reference it without re-fetching.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def resolve_tenant(state: AgentState) -> dict:
    """Stub: resolve and validate the tenant scope (§8.1)."""
    return {}
