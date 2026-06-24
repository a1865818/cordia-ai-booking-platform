"""Node: load_context — §8.2 Context Loading.

Loads tenant policy, customer context and session history so the downstream
intent-identification and capture nodes have full context available.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def load_context(state: AgentState) -> dict:
    """Stub: load tenant policy + customer context (§8.2)."""
    return {}
