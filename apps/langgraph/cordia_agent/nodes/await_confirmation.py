"""Node: await_confirmation — §8.9 Confirmation Gate.

Waits for the customer's explicit yes/no response after the read-back.
Confirmed drafts proceed to submit_intent; rejected drafts loop back to
capture_fields so the customer can amend details.
"""

from __future__ import annotations

from cordia_agent.state import AgentState  # noqa: TC001


async def await_confirmation(state: AgentState) -> dict:
    """Stub: gate on explicit customer confirmation before submit (§8.9)."""
    return {}


def route_after_await_confirmation(state: AgentState) -> str:
    """Router stub: always takes the happy-path to submit_intent.

    Real implementation will check the latest Confirmation record and loop back
    to capture_fields when the customer requests changes.
    """
    return "submit_intent"
