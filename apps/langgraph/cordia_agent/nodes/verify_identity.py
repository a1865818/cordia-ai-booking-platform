"""Node: verify_identity — §8.6 Identity Verification / §9.3 Grant Gate (Inv-11).

Obtains a platform-issued verification grant before any mutation can reach
SubmitIntent.  Caller ID alone NEVER suffices (Inv-11); a valid, in-scope,
unexpired platform grant is required.

Grant validation outcomes:
  VALID   → sets verification_grant_id, routes back to capture_fields (advance).
  EXPIRED → clears verification_grant_id, routes back to capture_fields (re-verify).
  INVALID → sets terminal_state="human_handoff", routes to human_handoff (Inv-11).

This node performs NO mutation and NEVER mints/advances operation_id (02-J owns that).

Pass-through guard: if the operation is not a mutation (create/inquire) this node
should not have been entered; we treat it as a pass-through that routes back to
capture_fields without a grant — documented deliberately so the router is
deterministic even on an unexpected entry.
"""

from __future__ import annotations

import logging
from datetime import UTC, datetime
from enum import Enum
from typing import TYPE_CHECKING

from langgraph.graph import END

from cordia_agent.state import AgentState, OperationType
from cordia_agent.tools.client_binding import (
    check_verification_call,
    parse_verification_grant,
)
from cordia_agent.tools.status_codes import VerificationAction


if TYPE_CHECKING:
    from cordia_agent.tools.contracts import VerificationGrantView


_log = logging.getLogger(__name__)


class GrantOutcome(str, Enum):
    """Outcome of validating a platform-issued verification grant (02-K, Inv-11)."""

    VALID = "valid"
    EXPIRED = "expired"
    INVALID = "invalid"


def _required_action(operation: OperationType | None) -> VerificationAction | None:
    """Map a booking OperationType to the required VerificationAction.

    Returns:
        The ``VerificationAction`` required for the operation, or ``None`` when
        the operation does not require a verification grant (create / inquire /
        unknown — pass-through cases).

    Spec:
        reschedule is a modify operation — both require MODIFY (§9.3).
    """
    if operation is OperationType.modify or operation is OperationType.reschedule:
        return VerificationAction.MODIFY
    if operation is OperationType.cancel:
        return VerificationAction.CANCEL
    # create / inquire / None → no verification required
    return None


def validate_grant(
    grant: VerificationGrantView | None,
    *,
    tenant_id: str | None,
    action: VerificationAction,
    now: datetime,
) -> GrantOutcome:
    """Validate a platform-issued verification grant against expected scope.

    Inv-11: caller ID alone is NEVER sufficient — a grant_id must be present.

    Args:
        grant: The ``VerificationGrantView`` from check_verification, or ``None``
            if the tool returned a non-ok envelope / pending / failed status.
        tenant_id: The session tenant ID to match against.
        action: The ``VerificationAction`` the grant must permit.
        now: The current UTC datetime (injected for deterministic tests).

    Returns:
        ``VALID``   when the grant is present, in-scope, and unexpired.
        ``EXPIRED`` when the grant is otherwise valid but has expired.
        ``INVALID`` when absent, missing grant_id, or out of scope.
    """
    # None grant or absent grant_id → no platform grant (Inv-11)
    if grant is None or grant.grant_id is None:
        return GrantOutcome.INVALID

    # Wrong tenant scope or wrong action → forged / mismatched grant (Inv-11)
    if grant.tenant_id != tenant_id or grant.allowed_action != action:
        return GrantOutcome.INVALID

    # Expiry check — strict (<=) so an exactly-expired grant is rejected
    if grant.expires_at <= now:
        return GrantOutcome.EXPIRED

    return GrantOutcome.VALID


async def verify_identity(state: AgentState) -> dict:
    """Obtain and validate a platform-issued verification grant (§9.3, Inv-11).

    Determines the required VerificationAction from ``state.operation``, calls
    ``check_verification`` to retrieve the grant, validates scope + expiry, and
    returns the appropriate state update.

    Pass-through guard: when the operation does not require verification (create /
    inquire / unknown) this node routes back to capture_fields with no grant — this
    situation indicates an unexpected entry and is logged as a warning.

    This node performs NO mutation and NEVER sets/advances operation_id (Inv-11,
    02-J owns that).

    Returns:
        State update dict.  Possible keys:
          - verification_grant_id (str | None)
          - verification_outcome (str)
          - terminal_state (str) — only on INVALID
    """
    required_action = _required_action(state.operation)

    # Pass-through: operation does not require a verification grant
    if required_action is None:
        _log.warning(
            "verify_identity entered for non-mutation operation=%s; "
            "routing back to capture_fields without grant (pass-through)",
            state.operation,
        )
        return {"verification_outcome": "pass_through"}

    if not state.verification_id:
        _log.warning(
            "verify_identity called without verification_id; treating as invalid"
        )
        return {
            "verification_outcome": "invalid",
            "terminal_state": "human_handoff",
        }

    env = await check_verification_call(state.verification_id, code=None)
    grant = parse_verification_grant(env)

    outcome = validate_grant(
        grant,
        tenant_id=state.tenant_id,
        action=required_action,
        now=datetime.now(UTC),
    )

    if outcome is GrantOutcome.VALID:
        _log.info(
            "verify_identity: VALID grant=%s tenant=%s action=%s",
            grant.grant_id if grant else None,
            state.tenant_id,
            required_action,
        )
        return {
            "verification_grant_id": grant.grant_id,  # type: ignore[union-attr]
            "verification_outcome": "valid",
        }

    if outcome is GrantOutcome.EXPIRED:
        _log.info(
            "verify_identity: EXPIRED grant for tenant=%s action=%s; re-verify",
            state.tenant_id,
            required_action,
        )
        return {
            "verification_grant_id": None,
            "verification_outcome": "expired",
        }

    # INVALID — Inv-11: forged / absent grant → human_handoff
    _log.warning(
        "verify_identity: INVALID grant for tenant=%s action=%s; escalating to human_handoff (Inv-11)",
        state.tenant_id,
        required_action,
    )
    return {
        "verification_outcome": "invalid",
        "terminal_state": "human_handoff",
    }


def route_after_verify_identity(state: AgentState) -> str:
    """Route after verify_identity based on verification_outcome.

    Routes:
      - ``"human_handoff"`` when outcome is "invalid" (or terminal_state set).
      - ``END`` when the grant has EXPIRED — re-verification needs a fresh code
        from the customer, which cannot arrive inside this turn.  Continuing to
        ``capture_fields`` sent the same mutation draft straight back here with
        the grant still cleared, which is a cycle, not a retry.
      - ``"capture_fields"`` for "valid" (advance) or "pass_through"
        (non-mutation operation — unexpected entry).
    """
    outcome = state.verification_outcome
    if outcome == "invalid" or state.terminal_state == "human_handoff":
        return "human_handoff"
    if outcome == "expired":
        return END
    return "capture_fields"
