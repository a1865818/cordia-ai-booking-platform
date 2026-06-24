"""Node: verify_identity — §8.6 Identity Verification.

Triggers an OOB verification challenge when the tenant policy requires the
customer to be authenticated before the operation can proceed.  On success
routes back to capture_fields; on failure routes to human_handoff.
"""

from __future__ import annotations

from cordia_agent.state import AgentState  # noqa: TC001


async def verify_identity(state: AgentState) -> dict:
    """Stub: initiate OOB identity verification challenge (§8.6)."""
    return {}


def route_after_verify_identity(state: AgentState) -> str:
    """Router stub: always takes the happy-path back to capture_fields.

    Real implementation will check the grant status from the verification
    tool and escalate to human_handoff if verification failed.
    """
    return "capture_fields"
