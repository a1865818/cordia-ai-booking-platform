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
from cordia_agent.state import AgentState, OperationType
from cordia_agent.tools.client_binding import (
    get_operation_status_call,
    parse_operation_status,
    submit_cancellation_call,
    submit_intent_call,
    submit_modification_call,
)
from cordia_agent.tools.contracts import (
    SubmitCancellationInput,
    SubmitConfirmedIntentInput,
    SubmitModificationInput,
)


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
        # The generic mapping for a 4xx the API did not give a typed code for
        # (400/401/403/422).  See _is_inconclusive: these are DEFINITIVE
        # rejections and spec 03 §4.3 classifies them as recoverable.
        "request_error",
        "conflict",
        # The booking changed under us (spec 08 §9.4.8).  Re-reading the target
        # and re-confirming is the fix, not a human.
        "stale_version",
    }
)

# Envelope statuses that mean "we do not know whether the mutation landed".
# ONLY these justify the Inv-5 lost-response dance (poll the operation, then
# resubmit the same durable mutation if it is not there).
#
# The bug this replaces: `_do_submit` treated *any* `env.ok == False` as a lost
# response.  `map_http_error` maps 422 to the generic `request_error`, so a
# server that had already read the payload and definitively refused it got a
# GET that 404s, followed by a RESUBMIT of the identical payload, followed by
# the identical 422 — and then `nonrecoverable` → `human_handoff`.  Two things
# wrong with that: a pointless duplicate durable-mutation attempt against a
# payload the server explicitly rejected, and a terminal handoff for a case
# spec 03 §4.3 says is recoverable and routable back to `capture_fields`.
_INCONCLUSIVE_STATUSES: frozenset[str] = frozenset(
    {
        "unavailable",  # transport failure, timeout, or 5xx
        "rate_limited",  # 429: the request may or may not have been processed
    }
)


def _is_inconclusive(status: str) -> bool:
    """Whether *status* leaves the mutation's fate genuinely unknown (Inv-5).

    A definitive rejection is not a lost response.  Recovery exists for the
    case where the request may have been applied and we never saw the answer —
    a timeout, a dropped connection, a 5xx.  A 422 means the server read the
    payload and said no; polling for an operation it never created and then
    sending the same payload again is duplicated work for a guaranteed
    identical refusal.
    """
    return status in _INCONCLUSIVE_STATUSES


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

    # The target gate, moved here from BookingRequest's constructor.  Requiring a
    # target at construction meant a mutation draft could not exist before the
    # caller had named the booking, which is the wrong end of the conversation
    # (see BookingRequest.require_target_for_mutations).  This is the point where
    # it genuinely matters: nothing leaves for the platform without one.
    try:
        rev.canonical_payload.require_target_for_mutations()
    except ValueError:
        _log.warning(
            "submit_intent: %s has no target_booking_id — refusing to submit",
            rev.canonical_payload.operation,
        )
        return {
            "submission_status": "validation_failed",
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
            grant_id=state.verification_grant_id,
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
        grant_id=state.verification_grant_id,
    )


def _sender_for(
    *,
    op_id: str,
    idem_key: str,
    rev: IntentRevision,
    conf: Confirmation,
    grant_id: str | None,
):
    """Return the coroutine factory that submits *rev* to the RIGHT endpoint.

    Returns a node-outcome dict instead when the mutation cannot lawfully be
    sent: a mutation with no grant is Inv-11 bypassed, and fails closed to a
    human rather than being posted anywhere.  A missing booking version is
    recoverable (re-read the target), so that routes back to capture_fields.
    """
    payload = rev.canonical_payload
    operation = payload.operation

    if operation is OperationType.create:
        inp = SubmitConfirmedIntentInput(
            operation_id=op_id,
            idempotency_key=idem_key,
            intent_id=rev.intent_id,
            intent_revision=rev.revision,
            intent_payload_hash=rev.payload_hash,
            revision=rev,
            confirmation=conf,
        )

        async def _create():
            return await submit_intent_call(inp)

        return _create

    # Every other operation acts on an existing booking and needs a grant.
    if not grant_id:
        _log.error(
            "submit_intent: %s reached submission with no verification grant (Inv-11); refusing",
            operation.value,
        )
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": "nonrecoverable",
            "terminal_state": "human_handoff",
        }
    target_id = payload.target_booking_id
    version = payload.target_booking_version
    if not target_id or version is None:
        _log.warning(
            "submit_intent: %s has no target id/version; back to capture",
            operation.value,
        )
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": "validation_failed",
        }

    if operation is OperationType.cancel:
        cancel_inp = SubmitCancellationInput(
            target_booking_id=target_id,
            expected_booking_version=version,
            operation_id=op_id,
            cancellation_idempotency_key=idem_key,
            reason_code="customer_request",
            grant_id=grant_id,
            confirmation=conf,
        )

        async def _cancel():
            return await submit_cancellation_call(cancel_inp)

        return _cancel

    modify_inp = SubmitModificationInput(
        target_booking_id=target_id,
        expected_booking_version=version,
        operation_id=op_id,
        modification_idempotency_key=idem_key,
        intent_revision=rev.revision,
        intent_payload_hash=rev.payload_hash,
        confirmation=conf,
        grant_id=grant_id,
    )

    async def _modify():
        return await submit_modification_call(modify_inp)

    return _modify


async def _do_submit(
    *,
    op_id: str,
    idem_key: str,
    rev: IntentRevision,
    conf: Confirmation,
    grant_id: str | None = None,
) -> dict:
    """Issue the actual submit call (single-flight) and map the result.

    Uses get_single_flight().run(op_id, factory) so concurrent callers for
    the same op_id share one real API call (Inv-5, §27.3).

    On lost response (non-ok envelope): attempts get_operation_status;
    if found → recover; if not → resubmit once with same ids (API dedups → one op).
    """
    # Which endpoint a confirmed intent goes to is decided by its OPERATION.
    # Everything used to go to the create endpoint, so a confirmed cancellation
    # was recorded by the API as operation_type=cancel with status=booked: a new
    # table, and the old one untouched.  The dedicated tools existed the whole
    # time; nothing called them.
    send = _sender_for(
        op_id=op_id, idem_key=idem_key, rev=rev, conf=conf, grant_id=grant_id
    )
    if isinstance(send, dict):
        return send  # a refusal, already shaped as a node outcome

    async def _factory():
        return await send()

    env = await get_single_flight().run(op_id, _factory)

    # Lost-response recovery, and ONLY for genuinely inconclusive outcomes.
    # A definitive 4xx skips straight to the status mapping below, which routes
    # it back to capture_fields per spec 03 §4.3.
    if not env.ok and _is_inconclusive(env.status):
        _log.warning(
            "submit_intent: submit outcome inconclusive (status=%s) — attempting "
            "recovery before any resubmit (Inv-5)",
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
        env = await send()

    # Map the submission envelope status to an outcome dict
    status_code = env.status

    # accepted / duplicate_accepted → submitted terminal (Inv-16)
    if status_code == "already_cancelled":
        _log.info("submit_intent: booking was already cancelled — op_id=%s", op_id)
        return {
            "operation_id": op_id,
            "idempotency_key": idem_key,
            "submission_status": status_code,
            "terminal_state": "submitted",
            "submission_message": "That one's already been cancelled, so there's nothing more to do.",
        }

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
