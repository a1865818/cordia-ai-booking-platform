"""Node: identify_intent — §8.3 Intent Identification.

Classifies the customer's utterance into one of the supported OperationTypes
(create / reschedule / modify / cancel) and seeds the draft accordingly.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def identify_intent(state: AgentState) -> dict:
    """Stub: classify customer intent into an OperationType (§8.3)."""
    return {}
