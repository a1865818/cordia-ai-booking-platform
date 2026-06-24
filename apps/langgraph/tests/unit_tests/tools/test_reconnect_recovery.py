"""Tools-level reconnect recovery tests (§27.1 Inv-2, spec §5.2).

Tests:
- recover-not-resubmit (Inv-2): after a submit, the agent can find the operation
  via get_operation_status (recovery path) rather than blindly resubmitting.
  Asserts the recovery path uses get_operation_status, not a second submit.
- same-key resubmit → duplicate_accepted (§5.2, Inv-2): a second submit with the
  same operation_id + idempotency_key returns duplicate_accepted from the fake API
  (idem_key dedup) → exactly ONE operation recorded in the stateful fake API.

This is a tools-level test (distinct from the 02-J node-level integration test),
focused on the contract boundary: the ToolClient + foreground tool layer.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.models.intent import Confirmation
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    SubmitConfirmedIntentInput,
    ToolResultEnvelope,
)
from cordia_agent.tools.foreground import (
    get_operation_status,
    submit_confirmed_intent,
)


# ---------------------------------------------------------------------------
# Stateful fake API for reconnect/dedup tests
# ---------------------------------------------------------------------------


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


class StatefulReconnectFakeApi:
    """Fake API that tracks submitted operations by (operation_id, idem_key).

    Serves:
    - POST /api/bookings: records the op; returns duplicate_accepted on replay.
    - GET /api/booking-operations/{op_id}: finds the recorded op, returns in_progress.

    Tracks all requests so tests can assert call counts.
    """

    def __init__(self) -> None:
        # Maps operation_id -> recorded_op dict
        self._ops: dict[str, dict[str, Any]] = {}
        # Maps idempotency_key -> operation_id for dedup
        self._idem_to_op: dict[str, str] = {}
        self.requests: list[httpx.Request] = []
        self.submit_count: int = 0
        self.status_poll_count: int = 0

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        method = request.method.upper()
        path = request.url.path

        if method == "POST" and path == "/api/bookings":
            return self._handle_submit(request)

        if method == "GET" and path.startswith("/api/booking-operations/"):
            op_id = path.split("/")[-1]
            return self._handle_get_status(op_id)

        return _json_response(404, {"error": "not_found"})

    def _handle_submit(self, request: httpx.Request) -> httpx.Response:
        self.submit_count += 1
        operation_id = request.headers.get("X-Operation-Id", "op_unknown")
        idem_key = request.headers.get("X-Idempotency-Key", "")

        # Dedup: same idem_key → duplicate_accepted (§5.2, Inv-2)
        if idem_key and idem_key in self._idem_to_op:
            existing_op_id = self._idem_to_op[idem_key]
            return _json_response(
                202,
                {
                    "status": "duplicate_accepted",
                    "operation_id": existing_op_id,
                    "intent_id": "intent_001",
                    "intent_revision": 1,
                    "status_url": f"/api/booking-operations/{existing_op_id}",
                    "submitted_at": "2026-06-25T10:00:00+00:00",
                },
            )

        # First submission — record the op
        op_record = {
            "operation_id": operation_id,
            "status": "in_progress",
            "pending": True,
            "terminal": False,
        }
        self._ops[operation_id] = op_record
        if idem_key:
            self._idem_to_op[idem_key] = operation_id

        return _json_response(
            202,
            {
                "status": "accepted",
                "operation_id": operation_id,
                "intent_id": "intent_001",
                "intent_revision": 1,
                "status_url": f"/api/booking-operations/{operation_id}",
                "submitted_at": "2026-06-25T10:00:00+00:00",
            },
        )

    def _handle_get_status(self, op_id: str) -> httpx.Response:
        self.status_poll_count += 1
        if op_id not in self._ops:
            return _json_response(
                404, {"status": "not_found", "error": "operation_not_found"}
            )

        op = self._ops[op_id]
        return _json_response(
            200,
            {
                "status": op.get("status", "in_progress"),
                "operation_id": op_id,
                "pending": op.get("pending", True),
                "terminal": op.get("terminal", False),
                "customer_message": "Your request is currently being processed.",
                "updated_at": "2026-06-25T10:00:00+00:00",
            },
        )


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _fake_tenant_context() -> TenantContext:
    return TenantContext(
        tenant_id="tenant_reconnect_test",
        channel=Channel.voice,
        channel_endpoint_id="ep_reconnect_001",
        configuration_version="v1",
        correlation_id="corr_reconnect_001",
        allowed_policy_ids=["policy_a"],
    )


def _minimal_confirmation(
    intent_id: str = "intent_001",
    intent_revision: int = 1,
    intent_payload_hash: str = "hash_reconnect_abc",
) -> Confirmation:
    return Confirmation(
        confirmation_id="conf_reconnect_001",
        intent_id=intent_id,
        intent_revision=intent_revision,
        intent_payload_hash=intent_payload_hash,
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=datetime.datetime(2026, 6, 25, 10, 0, tzinfo=datetime.UTC),
        source_turn_id="turn_reconnect_001",
        read_back_version="v1",
        verification_level="none",
    )


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def stateful_api() -> StatefulReconnectFakeApi:
    return StatefulReconnectFakeApi()


@pytest.fixture()
def reconnect_client(stateful_api: StatefulReconnectFakeApi) -> ToolClient:
    """ToolClient backed by StatefulReconnectFakeApi; registered + cleaned up."""
    transport = httpx.MockTransport(stateful_api)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_fake_tenant_context())
    set_tool_client(client)
    yield client
    reset_tool_client()


# ---------------------------------------------------------------------------
# recover-not-resubmit (Inv-2)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.asyncio
async def test_recover_not_resubmit(
    reconnect_client: ToolClient,
    stateful_api: StatefulReconnectFakeApi,
) -> None:
    """After a submit, the recovery path uses get_operation_status, NOT a second submit.

    Scenario:
    1. submit_confirmed_intent → accepted (op recorded in fake API).
    2. Simulate "lost response" scenario: the agent does NOT have the result.
    3. Instead of resubmitting, call get_operation_status → finds the op (recovery path).
    4. Assert the op was found and only ONE submit occurred.

    This proves the agent contract: on uncertain outcome, poll status rather than
    blindly resubmit (Inv-2: no duplicate durable mutations).
    """
    op_id = "op_reconnect_recover_001"
    idem_key = "idem_reconnect_recover_001"

    inp = SubmitConfirmedIntentInput(
        operation_id=op_id,
        idempotency_key=idem_key,
        intent_id="intent_reconnect_001",
        intent_revision=1,
        intent_payload_hash="hash_reconnect_abc",
        confirmation=_minimal_confirmation(
            intent_id="intent_reconnect_001",
            intent_revision=1,
            intent_payload_hash="hash_reconnect_abc",
        ),
    )

    # Step 1: submit (op recorded)
    submit_result = await submit_confirmed_intent.ainvoke(inp.model_dump(mode="json"))
    assert isinstance(submit_result, ToolResultEnvelope)
    assert submit_result.ok is True
    assert stateful_api.submit_count == 1

    # Step 2: the agent "lost" the response and wants to recover.
    # Recovery path: call get_operation_status (not resubmit).
    status_result = await get_operation_status.ainvoke({"operation_id": op_id})
    assert isinstance(status_result, ToolResultEnvelope)
    assert status_result.ok is True, (
        f"get_operation_status must find the recorded op; got ok={status_result.ok!r}, "
        f"status={status_result.status!r}"
    )

    # Step 3: assert recovery path used get_operation_status (not a second submit)
    assert stateful_api.submit_count == 1, (
        f"Only ONE submit should have occurred (no re-submit on recovery); "
        f"got submit_count={stateful_api.submit_count}"
    )
    assert stateful_api.status_poll_count == 1, (
        f"Exactly one status poll for recovery; got {stateful_api.status_poll_count}"
    )

    # The op should be found (existence confirmed via get_operation_status)
    assert op_id in stateful_api._ops, (
        "Op must exist in the fake API (was recorded by the first submit)"
    )


# ---------------------------------------------------------------------------
# same-key resubmit → duplicate_accepted (§5.2, Inv-2)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.asyncio
async def test_same_key_resubmit_returns_duplicate_accepted(
    reconnect_client: ToolClient,
    stateful_api: StatefulReconnectFakeApi,
) -> None:
    """§5.2, Inv-2: second submit with same operation_id + idem_key → duplicate_accepted.

    The fake API deduplicates by idempotency_key. A second call with the same key
    must return duplicate_accepted and NOT create a second operation record.
    This is the safe idempotent path that prevents duplicate durable mutations.
    """
    op_id = "op_idem_dup_001"
    idem_key = "idem_dup_001"

    inp = SubmitConfirmedIntentInput(
        operation_id=op_id,
        idempotency_key=idem_key,
        intent_id="intent_dup_001",
        intent_revision=1,
        intent_payload_hash="hash_dup_abc",
        confirmation=_minimal_confirmation(
            intent_id="intent_dup_001",
            intent_revision=1,
            intent_payload_hash="hash_dup_abc",
        ),
    )
    payload = inp.model_dump(mode="json")

    # First submit → accepted
    first_result = await submit_confirmed_intent.ainvoke(payload)
    assert isinstance(first_result, ToolResultEnvelope)
    assert first_result.ok is True
    assert first_result.status == "accepted", (
        f"First submit must return 'accepted', got: {first_result.status!r}"
    )

    # Second submit with SAME operation_id + idem_key → duplicate_accepted
    second_result = await submit_confirmed_intent.ainvoke(payload)
    assert isinstance(second_result, ToolResultEnvelope)
    assert second_result.ok is True, (
        f"duplicate_accepted must still be ok=True; got: {second_result.ok!r}"
    )
    assert second_result.status == "duplicate_accepted", (
        f"Inv-2/§5.2: second call with same key must return 'duplicate_accepted', "
        f"got: {second_result.status!r}"
    )

    # Exactly ONE operation must be recorded (dedup guarantee)
    assert len(stateful_api._ops) == 1, (
        f"Exactly ONE operation record expected after dedup; got {len(stateful_api._ops)}"
    )

    # Both submits hit the transport (fake API decides dedup server-side — Inv-2)
    assert stateful_api.submit_count == 2, (
        f"Both requests must reach the transport (server-side dedup); "
        f"got submit_count={stateful_api.submit_count}"
    )
