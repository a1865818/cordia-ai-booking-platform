"""Node: read_back — §8.8 Read-Back / Summary.

Renders a human-readable summary of the booking draft and presents it to the
customer for confirmation before submission.  Always advances to
await_confirmation.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def read_back(state: AgentState) -> dict:
    """Stub: render booking draft summary for customer confirmation (§8.8)."""
    return {}
