"""Node: human_handoff — §8.11 Human Handoff (HumanHandoff terminal, 02-K).

Packages the current draft + session context into a HandoffPackage and
triggers escalation to a human agent.  Sets terminal_state="human_handoff"
and routes unconditionally to END.

Idempotency (Inv-9): the handoff_idempotency_key is derived DETERMINISTICALLY
from session_id + reason_code, so a retry returns the existing work item rather
than creating a duplicate.
"""

from __future__ import annotations

import hashlib
import logging

from cordia_agent.models.handoff import HandoffPackage
from cordia_agent.state import AgentState  # noqa: TC001
from cordia_agent.tools.client_binding import (
    parse_handoff_result,
    request_human_handoff_call,
)
from cordia_agent.tools.contracts import HandoffRequest


_log = logging.getLogger(__name__)

_DEFAULT_REASON_CODE = "verification_failed"
_DEFAULT_REASON_TEXT = (
    "Verification or authorization failed; escalating to human agent."
)


def _stable_idempotency_key(session_id: str | None, reason_code: str) -> str:
    """Derive a stable, deterministic handoff idempotency key (Inv-9).

    The key is a SHA-256 hex digest of ``session_id:reason_code`` so that:
      - The same session + reason always produces the same key.
      - A retry does NOT mint a new key — the platform returns the existing
        work item (Inv-9).
      - The key is opaque to the platform and carries no PII.

    Args:
        session_id: The dialog session identifier; defaults to "unknown" when None.
        reason_code: The structured reason code for the handoff.

    Returns:
        A stable 64-char hex string derived from the inputs.
    """
    raw = f"{session_id or 'unknown'}:{reason_code}"
    return hashlib.sha256(raw.encode()).hexdigest()


def build_handoff_package(state: AgentState, *, reason: str) -> HandoffPackage:
    """Build a well-formed HandoffPackage from the current agent state.

    Args:
        state: The current ``AgentState`` at the time of escalation.
        reason: A human-readable reason string for the handoff (§8.12).

    Returns:
        A ``HandoffPackage`` with reason, draft_snapshot, and source_turn_ids.
    """
    source_turn_ids = [state.current_turn_id] if state.current_turn_id else []
    return HandoffPackage(
        reason=reason,
        draft_snapshot=state.draft,
        source_turn_ids=source_turn_ids,
    )


async def human_handoff(state: AgentState) -> dict:
    """Package context for human escalation; sets terminal_state (§8.11, 02-K).

    Builds a ``HandoffPackage`` from the current state, constructs a
    ``HandoffRequest`` with a STABLE idempotency key (Inv-9), invokes
    ``request_human_handoff``, and returns the terminal state update.

    Route: unconditional edge to END is in graph.py — no router needed.

    Returns:
        State update dict with ``terminal_state``, ``handoff_package``,
        and optionally ``handoff_result`` when the tool call succeeds.
    """
    reason_code = state.handoff_reason_code or _DEFAULT_REASON_CODE
    reason_text = _DEFAULT_REASON_TEXT

    pkg = build_handoff_package(state, reason=reason_text)

    idempotency_key = _stable_idempotency_key(state.session_id, reason_code)

    req = HandoffRequest(
        session_id=state.session_id or "unknown",
        reason_code=reason_code,
        handoff_idempotency_key=idempotency_key,
        recommended_action=None,
    )

    _log.info(
        "human_handoff: escalating session=%s reason_code=%s idem_key=%s",
        state.session_id,
        reason_code,
        idempotency_key,
    )

    env = await request_human_handoff_call(req)
    result = parse_handoff_result(env)

    update: dict = {
        "terminal_state": "human_handoff",
        "handoff_package": pkg.model_dump(mode="json"),
    }
    if result is not None:
        update["handoff_result"] = result.model_dump(mode="json")
    else:
        _log.warning(
            "human_handoff: request_human_handoff returned non-ok; "
            "terminal_state still set (session=%s)",
            state.session_id,
        )

    return update
