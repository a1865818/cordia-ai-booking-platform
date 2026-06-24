"""Node: clarify — §8.5 Clarification.

Asks the customer a targeted clarifying question when a captured field value
is ambiguous or invalid, then returns to capture_fields to re-evaluate the
updated draft.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def clarify(state: AgentState) -> dict:
    """Stub: issue a clarifying question for an ambiguous field (§8.5)."""
    return {}
