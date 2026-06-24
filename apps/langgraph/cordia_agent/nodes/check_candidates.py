"""Node: check_candidates — §8.7 Candidate Availability Check.

Calls the availability tool to fetch concrete booking slots that satisfy the
draft constraints.  Routes back to capture_fields when no candidates match, or
forwards to read_back when at least one valid candidate is found.
"""

from __future__ import annotations

from cordia_agent.state import AgentState  # noqa: TC001


async def check_candidates(state: AgentState) -> dict:
    """Stub: check candidate availability for the draft (§8.7)."""
    return {}


def route_after_check_candidates(state: AgentState) -> str:
    """Router stub: always takes the happy-path to read_back.

    Real implementation will inspect the availability result and loop back to
    capture_fields when no matching slots exist.
    """
    return "read_back"
