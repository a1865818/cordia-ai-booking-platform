"""Integration tests for reconnect recovery in submit_intent (02-J, Inv-2/5).

Uses a stateful fake API that tracks submitted operations by idem_key and
serves get_operation_status.  Exercises the two reconnect branches:

  - lost-response → found → recover (no resubmit):
    Server records the op, client "loses" the first response; reconnect
    polls get_operation_status, finds it → recovered without a second submit.

  - lost-response → not found → resubmit same key → one op:
    Server has no record; client resubmits with SAME op_id+idem_key;
    fake deduplicates by idem_key → duplicate_accepted → exactly ONE operation.

Spec refs: §8.9, §10.1, §27.3, Inv-2, Inv-5.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.checkpoint import (
    SessionCheckpointer,
    reset_session_checkpointer,
    set_session_checkpointer,
)
from cordia_agent.models.intent import Confirmation, IntentRevision
from cordia_agent.nodes.submit_intent import submit_intent
from cordia_agent.recovery import reset_single_flight
from cordia_agent.revisions.manager import build_revision
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_NOW = datetime.datetime(2026, 6, 25, 10, 0, 0, tzinfo=datetime.UTC)
_SUBMITTED_AT = "2026-06-25T10:00:00+00:00"
_UPDATED_AT = "2026-06-25T10:01:00+00:00"

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_rr_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_rr_001",
    configuration_version="v1",
    correlation_id="corr_rr_test_001",
    session_id="sess_rr_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


def _make_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_rr_test",
        session_id="sess_rr_test",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_rr_001",
            location_id="loc_rr_001",
            starts_at=datetime.datetime(2026, 7, 10, 9, 0, tzinfo=datetime.UTC),
            party_size=3,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_revision(draft: BookingDraft) -> IntentRevision:
    return build_revision(
        intent_id="intent_rr_001",
        revision=1,
        canonical_payload=draft.request,
        schema_version=draft.schema_version,
        tenant_policy_version=draft.tenant_policy_version,
        source_session_id=draft.session_id,
        created_at=_NOW,
    )


def _make_confirmation(rev: IntentRevision) -> Confirmation:
    return Confirmation(
        confirmation_id="conf_rr_001",
        intent_id=rev.intent_id,
        intent_revision=rev.revision,
        intent_payload_hash=rev.payload_hash,
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=_NOW,
        source_turn_id="turn_rr_001",
        read_back_version="hash_rr_001",
        verification_level="unverified",
    )


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _pre_checkpointed_state(
    op_id: str,
    idem_key: str,
    cp: SessionCheckpointer,
) -> AgentState:
    """Build a state with op_id+idem_key already checkpointed (simulates prior turn)."""
    draft = _make_draft()
    rev = _make_revision(draft)
    conf = _make_confirmation(rev)
    state = AgentState(
        tenant_id="tenant_rr_test",
        session_id="sess_rr_test",
        draft=draft,
        current_revision=rev.model_dump(mode="json"),
        confirmation=conf.model_dump(mode="json"),
        operation_id=op_id,
        idempotency_key=idem_key,
    )
    # Persist the ids so the checkpointer sees them as durable
    cp.checkpoint_material_update(state)
    return state


# ---------------------------------------------------------------------------
# Stateful fake API — tracks submissions by idem_key
# ---------------------------------------------------------------------------


class StatefulFakeApi:
    """Fake API that records submitted operations and serves get_operation_status.

    Tracks:
      - ``submit_count``  — total POST /api/bookings requests received.
      - ``_operations``   — dict[idem_key → operation_id] of recorded operations.

    get_operation_status returns the operation if the path op_id was submitted.
    """

    def __init__(
        self, *, lose_response: bool = False, lose_status: bool = False
    ) -> None:
        """Initialise the stateful fake API.

        Args:
            lose_response: If True, the submit response is simulated as "lost"
                (returns a non-ok 500 envelope) even though the server stores
                the operation.  get_operation_status will find it.
            lose_status: If True, get_operation_status always returns not_found,
                simulating the case where the submit was never received AND
                the poll returns nothing → resubmit path.
        """
        self.submit_count: int = 0
        self._operations: dict[str, str] = {}  # idem_key → op_id
        self._lose_response = lose_response
        self._lose_status = lose_status

    def __call__(self, req: httpx.Request) -> httpx.Response:
        method = req.method.upper()
        path = req.url.path

        # POST /api/bookings → submit
        if method == "POST" and path == "/api/bookings":
            self.submit_count += 1
            body = json.loads(req.content)
            op_id = body.get("operation_id", "")
            idem_key = body.get("idempotency_key", "")

            # Dedup by idem_key
            if idem_key in self._operations:
                # Already submitted → duplicate_accepted
                return _json_response(
                    202,
                    {
                        "status": "duplicate_accepted",
                        "ok": True,
                        "operation_id": self._operations[idem_key],
                        "intent_id": body.get("intent_id", ""),
                        "intent_revision": body.get("intent_revision", 1),
                        "status_url": f"/api/booking-operations/{self._operations[idem_key]}",
                        "submitted_at": _SUBMITTED_AT,
                        "correlation_id": "corr_dedup",
                    },
                )

            # Record the operation
            self._operations[idem_key] = op_id

            if self._lose_response:
                # Simulate a lost response (server stores, client gets 500)
                return _json_response(
                    500,
                    {
                        "status": "unavailable",
                        "ok": False,
                        "correlation_id": "corr_lost",
                    },
                )

            return _json_response(
                202,
                {
                    "status": "accepted",
                    "ok": True,
                    "operation_id": op_id,
                    "intent_id": body.get("intent_id", ""),
                    "intent_revision": body.get("intent_revision", 1),
                    "status_url": f"/api/booking-operations/{op_id}",
                    "submitted_at": _SUBMITTED_AT,
                    "correlation_id": "corr_ok",
                },
            )

        # GET /api/booking-operations/{op_id} → status poll
        if method == "GET" and path.startswith("/api/booking-operations/"):
            queried_op_id = path.split("/")[-1]

            if self._lose_status:
                return _json_response(
                    404,
                    {"status": "not_found", "ok": False, "correlation_id": "c"},
                )

            # Check if this op_id was ever submitted
            if queried_op_id in self._operations.values():
                return _json_response(
                    200,
                    {
                        "status": "accepted",
                        "ok": True,
                        "operation_id": queried_op_id,
                        "booking_id": None,
                        "booking_reference": None,
                        "availability_mode": None,
                        "customer_message": "Your request has been submitted and is being processed.",
                        "pending": True,
                        "terminal": False,
                        "updated_at": _UPDATED_AT,
                        "correlation_id": "corr_status",
                    },
                )

            return _json_response(
                404,
                {"status": "not_found", "ok": False, "correlation_id": "c"},
            )

        return _json_response(
            404, {"status": "not_found", "ok": False, "correlation_id": "c"}
        )


# ---------------------------------------------------------------------------
# Fixture
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_after_test():
    """Reset shared state after each test."""
    yield
    reset_tool_client()
    reset_session_checkpointer()
    reset_single_flight()


# ---------------------------------------------------------------------------
# Test 1: lost-response → found → recover (no resubmit) — Inv-5
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_lost_response_found_recover_no_resubmit() -> None:
    """Inv-5: lost response; server recorded op; reconnect finds it → no 2nd submit.

    Sequence:
      1. Server stores the op but returns 500 (lose_response=True).
      2. Client's submit call fails (non-ok envelope).
      3. Node calls get_operation_status → found.
      4. Recovered: terminal_state='submitted', submit_count stays 1.
    """
    fake_api = StatefulFakeApi(lose_response=True)
    set_tool_client(_make_client(fake_api))

    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    op_id = "op_rr_lost_found"
    idem_key = "idem_rr_lost_found"
    state = _pre_checkpointed_state(op_id, idem_key, cp)

    # The node tries to recover the pre-existing operation on reconnect branch;
    # get_operation_status returns not_found FIRST (since no submit yet), so it falls
    # through to submit. Submit returns 500 (lose_response), then recovery finds it.
    result = await submit_intent(state)

    # Submit was called exactly once (the initial call that "lost" the response)
    assert fake_api.submit_count == 1, (
        f"Inv-5: expected submit_count=1 (no resubmit after recovery); got {fake_api.submit_count}"
    )
    assert result.get("terminal_state") == "submitted", (
        f"Expected submitted after recovery; got: {result}"
    )


# ---------------------------------------------------------------------------
# Test 2: lost-response → not found → resubmit same key → one op — Inv-2/5
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_lost_response_not_found_resubmit_same_key_one_op() -> None:
    """Inv-2/5: lost response + status poll not_found → resubmit SAME key → dedup → ONE op.

    Drives the genuine resubmit-after-not-found path with lose_response + lose_status:
      1. Reconnect poll get_operation_status(op_id) → not_found (lose_status) → fall through to submit.
      2. Submit #1 records the op (idem_key→op_id) but returns 500 (lose_response) — lost response.
      3. Post-submit recovery poll → not_found (lose_status) → genuine resubmit with the SAME op_id+idem_key.
      4. Submit #2 hits the idem_key dedup branch → duplicate_accepted (202).
    Expected: submit_count == 2, exactly ONE operation recorded, op_id reused verbatim (Inv-2),
    terminal 'submitted' with submission_status 'duplicate_accepted' (Inv-5).
    """
    fake_api = StatefulFakeApi(lose_response=True, lose_status=True)
    set_tool_client(_make_client(fake_api))

    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    op_id = "op_rr_resubmit"
    idem_key = "idem_rr_resubmit"
    state = _pre_checkpointed_state(op_id, idem_key, cp)

    result = await submit_intent(state)

    # The resubmit hit the idem_key dedup branch → duplicate_accepted, terminal submitted (Inv-5).
    assert result.get("terminal_state") == "submitted", (
        f"Expected submitted after resubmit/dedup; got {result}"
    )
    assert result.get("submission_status") == "duplicate_accepted", (
        f"Resubmit must surface duplicate_accepted; got {result.get('submission_status')!r}"
    )
    # Two POSTs reached the server: the lost-response submit + the dedup'd resubmit.
    assert fake_api.submit_count == 2, (
        f"Expected exactly 2 submit calls (lost + resubmit); got {fake_api.submit_count}"
    )
    # Despite two submits, exactly ONE operation exists (idem_key dedup).
    assert len(fake_api._operations) == 1, (
        f"Inv-2/5: exactly ONE operation should exist; got {len(fake_api._operations)}"
    )
    # The idem_key maps to the original op_id — never re-minted (Inv-2).
    assert fake_api._operations.get(idem_key) == op_id, (
        f"Inv-2: idem_key must map to original op_id={op_id!r}"
    )
