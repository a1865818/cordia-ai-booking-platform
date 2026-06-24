"""Node: human_handoff — §8.11 Human Handoff (HumanHandoff terminal).

Packages the current draft + session context into a HandoffPackage and
triggers escalation to a human agent.  Sets terminal_state="human_handoff"
and routes unconditionally to END.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def human_handoff(state: AgentState) -> dict:
    """Stub: package context for human escalation; sets terminal_state (§8.11)."""
    return {"terminal_state": "human_handoff"}
