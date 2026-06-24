"""Node: submit_intent — §8.10 Intent Submission (Submitted terminal).

Calls the SubmitConfirmedIntent tool with the canonical booking draft.  On
acceptance sets terminal_state="submitted" and routes to END (Submitted
terminal).  On conflict routes back to capture_fields; on unresolvable error
escalates to human_handoff.
"""

from __future__ import annotations

from langgraph.graph import END

from cordia_agent.state import AgentState  # noqa: TC001


async def submit_intent(state: AgentState) -> dict:
    """Stub: submit the confirmed booking draft; sets terminal_state on success (§8.10)."""
    return {"terminal_state": "submitted"}


def route_after_submit_intent(state: AgentState) -> str:
    """Router stub: always takes the happy-path to END (Submitted).

    Real implementation will inspect the SubmitConfirmedIntent tool result and
    route to capture_fields on conflict or human_handoff on unresolvable error.
    All three targets are enumerated in the path_map.
    """
    return END
