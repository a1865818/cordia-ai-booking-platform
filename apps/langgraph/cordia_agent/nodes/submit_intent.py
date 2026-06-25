"""Node: submit_intent — §8.9/§8.10 Intent Submission (Submitted terminal).

Mints operation_id + idempotency_key ONCE (Inv-2), checkpoints BEFORE submit
(§10.1), calls submit_confirmed_intent via SingleFlight (Inv-5, §27.3), and
handles lost-response recovery via get_operation_status (Inv-5).

Routing:
  - terminal "submitted"        → END
  - validation/policy/conflict  → capture_fields (fix + new revision + re-confirm)
  - nonrecoverable / checkpoint → human_handoff

Invariants enforced:
  Inv-2  — mint op_id+idem_key ONCE; checkpoint BEFORE submit; refuse to
            submit if checkpoint not durable; NEVER re-mint on retry/reconnect.
  Inv-5  — on a lost response, recover via get_operation_status BEFORE any
            resubmit; SingleFlight guards the concurrent reconnect race.
  §8.8   — refuse to submit unless confirmation_binds_revision(conf, rev).
  Inv-16 — 202 accepted → "I've submitted your request" wording via
            status_wording(ACCEPTED); NEVER "confirmed"/"booked".
"""

from __future__ import annotations

import logging

from langgraph.graph import END

from cordia_agent.checkpoint import get_session_checkpointer
from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.models.intent import (
    Confirmation,
    IntentRevision,
    confirmation_binds_revision,
)
from cordia_agent.readback.renderer import status_wording
from cordia_agent.recovery import (
    get_single_flight,
    mint_idempotency_key,
    mint_operation_id,
)
from cordia_agent.state import AgentState  # noqa: TC001
from cordia_agent.tools.client_binding import (
    get_operation_status_call,
    parse_operation_status,
    submit_intent_call,
)
from cordia_agent.tools.contracts import SubmitConfirmedIntentInput


_log = logging.getLogger(__name__)

# Submission statuses that map to terminal human_handoff
_NONRECOVERABLE_STATUSES: frozenset[str] = frozenset(
    {"nonrecoverable", "unavailable", "unavailable_after_recovery", "checkpoint_failed"}
)

# Submission statuses that map back to capture_fields (fix + new revision + re-confirm)
_CAPTURE_FIELDS_STATUSES: frozenset[str] = frozenset(
    {
        "validation_failed",
        "confirmation_invalid",
        "policy_failed",
        "idempotency_conflict",
    }
)


async def submit_intent(state: AgentState) -> dict:
    """Submit the confirmed booking intent with Inv-2/5/§8.8/Inv-16 enforcement.

    Steps:
    1. §8.8 guard — refuse if confirmation does not bind the current revision.
    2. Reconnect branch — if op_id already minted+checkpointed, recover first.
    3. First-submit branch — mint ids, checkpoint BEFORE submit (Inv-2).
    4. Submit via SingleFlight (Inv-5, §27.3).
    5. Lost-response recovery — if submit response is inconclusive, poll status.
    6. Map status → outcome dict.
    """
    # ------------------------------------------------------------------
    # §8.8 guard: confirmation must bind the current revision exactly.
    # ------------------------------------------------------------------
    rev_dict = state.current_revision
    conf_dict = state.confirmation

    if not rev_dict or not conf_dict:
        _log.warning(
            "submit_intent: missing current_revision or confirmation — cannot submit"
        )
        return {
            "submission_status": "confirmation_invalid",
        }

    try:
        rev = IntentRevision.model_validate(rev_dict)
        conf = Confirmation.model_validate(conf_dict)
    except Exception:
        _log.exception("submit_intent: failed to parse revision/confirmation models")
        return {
            "submission_status": "confirmation_invalid",
        }

    if not confirmation_binds_revision(conf, rev):
        _log.warning(
            "submit_intent: §8.8 guard — confirmation does not bind revision "
            "(intent_id=%s rev=%s)",
            rev.intent_id,
            rev.revision,
        )
        return {
            "submission_status": "confirmation_invalid",
        }

    cp = get_session_checkpointer()

    # ------------------------------------------------------------------
    # Reconnect branch: op_id already minted → recover BEFORE resubmit.
    # ------------------------------------------------------------------
    if state.operation_id is not None:
        op_id = state.operation_id
        idem_key = state.idempotency_key  # guaranteed non-None when op_id is set

        # Check if we are already at a terminal state (should not re-submit)
        if state.terminal_state == "submitted":
            _log.info("submit_intent: already terminal 'submitted', skipping re-submit")
            return {
                "submission_status": "accepted",
                "terminal_state": "submitted",
                "submission_message": status_wording(
                    NormalizedOperationStatus.ACCEPTED
                ),
            }

        _log.info(
            "submit_intent: reconnect branch — polling get_operation_status for op_id=%s",
            op_id,
        )
        env = await get_operation_status_call(op_id)
        op_status_view = parse_operation_status(env)

        if op_status_view is not None:
            # Operation found → recover without resubmit (Inv-5)
            _log.info(
                "submit_intent: reconnect recovery succeeded — status=%s",
                op_status_view.status,
            )
            return _build_outcome_from_operation_status(op_status_view)

        # Not found → fall through to resubmit with SAME op_id+idem_key (Inv-2)
        _log.info(
            "submit_intent: get_operation_status returned not_found — resubmitting "
            "with same op_id=%s (Inv-2)",
            op_id,
        )
        return await _do_submit(
            op_id=op_id,
            idem_key=idem_key,
            rev=rev,
            conf=conf,
        )

    # ------------------------------------------------------------------
    # First-submit branch: mint ids, checkpoint BEFORE submit (Inv-2).
    # ------------------------------------------------------------------
    op_id = mint_operation_id()
    idem_key = mint_idempotency_key()
    _log.info("submit_intent: first-submit — minted op_id=%s", op_id)

    # Build updated state snapshot with the new ids
    working = state.model_copy(
        update={"operation_id": op_id, "idempotency_key": idem_key}
    )

    # Checkpoint BEFORE submit (Inv-2, §10.1)
    try:
        cp.checkpoint_material_update(working)
    except Exception:
        _log.exception(
            "submit_intent: checkpoint_material_update raised — refusing to submit"
        )
        return {
            "submission_status": "checkpoint_failed",
            "terminal_state": "human_handoff",
        }

    # Verify the write is durable (Inv-2)
    if cp.requires_pre_submit_checkpoint(working):
        _log.error(
            "submit_intent: Inv-2 violated — pre-submit checkpoint not durable for op_id=%s; "
            "refusing to submit",
            op_id,
        )
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": "checkpoint_failed",
            "terminal_state": "human_handoff",
        }

    return await _do_submit(
        op_id=op_id,
        idem_key=idem_key,
        rev=rev,
        conf=conf,
    )


async def _do_submit(
    *,
    op_id: str,
    idem_key: str,
    rev: IntentRevision,
    conf: Confirmation,
) -> dict:
    """Issue the actual submit call (single-flight) and map the result.

    Uses get_single_flight().run(op_id, factory) so concurrent callers for
    the same op_id share one real API call (Inv-5, §27.3).

    On lost response (non-ok envelope): attempts get_operation_status;
    if found → recover; if not → resubmit once with same ids (API dedups → one op).
    """
    inp = SubmitConfirmedIntentInput(
        operation_id=op_id,
        idempotency_key=idem_key,
        intent_id=rev.intent_id,
        intent_revision=rev.revision,
        intent_payload_hash=rev.payload_hash,
        confirmation=conf,
    )

    async def _factory():
        return await submit_intent_call(inp)

    env = await get_single_flight().run(op_id, _factory)

    # Check for lost / transport error response
    if not env.ok:
        _log.warning(
            "submit_intent: submit returned non-ok (status=%s) — attempting recovery",
            env.status,
        )
        recovery_env = await get_operation_status_call(op_id)
        op_status_view = parse_operation_status(recovery_env)
        if op_status_view is not None:
            _log.info(
                "submit_intent: post-submit recovery succeeded — status=%s",
                op_status_view.status,
            )
            return _build_outcome_from_operation_status(op_status_view)

        # Not found after lost response → genuine resubmit with same ids; API dedups (Inv-2/5)
        _log.info(
            "submit_intent: post-submit recovery not_found — resubmitting same op_id=%s",
            op_id,
        )
        env = await submit_intent_call(inp)

    # Map the submission envelope status to an outcome dict
    status_code = env.status

    # accepted / duplicate_accepted → submitted terminal (Inv-16)
    if status_code in ("accepted", "duplicate_accepted"):
        _log.info("submit_intent: accepted — op_id=%s status=%s", op_id, status_code)
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": status_code,
            "terminal_state": "submitted",
            "submission_message": status_wording(NormalizedOperationStatus.ACCEPTED),
        }

    # validation / policy / idempotency conflict → capture_fields
    if status_code in _CAPTURE_FIELDS_STATUSES:
        _log.warning(
            "submit_intent: submission failed with recoverable status=%s", status_code
        )
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": status_code,
        }

    # Anything else → nonrecoverable → human_handoff
    _log.error("submit_intent: nonrecoverable submission status=%s", status_code)
    return {
        "operation_id": op_id,
        "idempotency_key": idem_key,
        "submission_status": "nonrecoverable",
        "terminal_state": "human_handoff",
    }


def _build_outcome_from_operation_status(op_status_view) -> dict:
    """Build the node return dict from a recovered OperationStatusView."""
    op_dict = op_status_view.model_dump(mode="json")

    # Terminal operation statuses we treat as "submitted"
    if op_status_view.status in (
        NormalizedOperationStatus.ACCEPTED,
        NormalizedOperationStatus.IN_PROGRESS,
        NormalizedOperationStatus.BOOKED,
        NormalizedOperationStatus.WAITLISTED,
    ):
        return {
            "submission_status": "accepted",
            "terminal_state": "submitted",
            "submission_message": status_wording(NormalizedOperationStatus.ACCEPTED),
            "operation_status": op_dict,
        }

    # Rejected / expired / failed → human_handoff
    if op_status_view.status in (
        NormalizedOperationStatus.REJECTED,
        NormalizedOperationStatus.EXPIRED,
        NormalizedOperationStatus.FAILED,
    ):
        return {
            "submission_status": "nonrecoverable",
            "terminal_state": "human_handoff",
            "operation_status": op_dict,
        }

    # Pending states (awaiting_user_action, alternatives_required) → treat as submitted
    return {
        "submission_status": "accepted",
        "terminal_state": "submitted",
        "submission_message": status_wording(NormalizedOperationStatus.ACCEPTED),
        "operation_status": op_dict,
    }


def route_after_submit_intent(state: AgentState) -> str:
    """Route after submit_intent based on terminal_state and submission_status.

    Returns:
        - ``END`` when ``terminal_state == "submitted"``.
        - ``"human_handoff"`` when ``terminal_state == "human_handoff"``
          (incl. checkpoint_failed / nonrecoverable).
        - ``"capture_fields"`` otherwise (validation failed, needs correction).
    """
    if state.terminal_state == "submitted":
        return END
    if state.terminal_state == "human_handoff":
        return "human_handoff"
    # submission_status in {validation_failed, confirmation_invalid,
    # policy_failed, idempotency_conflict} → fix + new revision + re-confirm
    return "capture_fields"
