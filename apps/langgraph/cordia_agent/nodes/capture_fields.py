"""Node: capture_fields — §8.4 Field Capture Loop.

Drives the slot-filling dialogue: asks for missing required fields, validates
each response, and routes to clarify, verify_identity, check_candidates or
read_back once the draft is complete.
"""

from __future__ import annotations

from cordia_agent.state import AgentState  # noqa: TC001


async def capture_fields(state: AgentState) -> dict:
    """Stub: slot-filling loop — collect required booking fields (§8.4)."""
    return {}


def route_after_capture_fields(state: AgentState) -> str:
    """Router stub: always takes the happy-path to read_back.

    Real implementation will inspect draft completeness and validation state.
    All four targets are enumerated in the path_map so the compiled graph
    statically records every edge.
    """
    return "read_back"
