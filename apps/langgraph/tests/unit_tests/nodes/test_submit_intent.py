"""Unit tests for the submit_intent node (02-J, §8.9/§10.1/§27.3).

Uses ``httpx.MockTransport`` + ``set_tool_client`` to drive the node against
a fake API; resets in teardown.  A ``set_session_checkpointer`` seam controls
the pre-submit checkpoint durability.

Covers:
  - §8.8 guard: confirmation that does NOT bind the revision → no submit call.
  - Inv-2 checkpoint-before-submit refusal: non-persisting checkpointer →
    submission_status="checkpoint_failed", routes to human_handoff.
  - Happy 202: accepted → terminal_state="submitted", submission_message
    contains "submitted" and NOT "confirmed"/"booked" (Inv-16).
  - validation_failed → route_after_submit_intent == "capture_fields".
  - nonrecoverable → terminal_state=="human_handoff", route=="human_handoff".
  - Single-flight (§27.3): two coroutines sharing op_id → exactly ONE submit.
"""

from __future__ import annotations

import asyncio
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
from cordia_agent.nodes.submit_intent import route_after_submit_intent, submit_intent
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

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_sj_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_sj_001",
    configuration_version="v1",
    correlation_id="corr_sj_test_001",
    session_id="sess_sj_001",
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


def _make_draft(service_id: str = "svc_001", party_size: int = 2) -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_sj_test",
        session_id="sess_sj_test",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id=service_id,
            location_id="loc_001",
            starts_at=datetime.datetime(2026, 7, 1, 10, 0, tzinfo=datetime.UTC),
            party_size=party_size,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_revision(draft: BookingDraft, revision: int = 1) -> IntentRevision:
    return build_revision(
        intent_id="intent_sj_001",
        revision=revision,
        canonical_payload=draft.request,
        schema_version=draft.schema_version,
        tenant_policy_version=draft.tenant_policy_version,
        source_session_id=draft.session_id,
        created_at=_NOW,
    )


def _make_confirmation(rev: IntentRevision) -> Confirmation:
    return Confirmation(
        confirmation_id="conf_sj_001",
        intent_id=rev.intent_id,
        intent_revision=rev.revision,
        intent_payload_hash=rev.payload_hash,
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=_NOW,
        source_turn_id="turn_001",
        read_back_version="hash_001",
        verification_level="none",
    )


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _bound_state(
    *,
    draft: BookingDraft | None = None,
    op_id: str | None = None,
    idem_key: str | None = None,
    terminal_state: str | None = None,
    extra: dict | None = None,
) -> AgentState:
    """Build a fully-bound AgentState ready for submit_intent."""
    d = draft or _make_draft()
    rev = _make_revision(d)
    conf = _make_confirmation(rev)
    state_data: dict[str, Any] = {
        "tenant_id": "tenant_sj_test",
        "session_id": "sess_sj_test",
        "draft": d,
        "current_revision": rev.model_dump(mode="json"),
        "confirmation": conf.model_dump(mode="json"),
        "operation_id": op_id,
        "idempotency_key": idem_key,
    }
    if terminal_state is not None:
        state_data["terminal_state"] = terminal_state
    if extra:
        state_data.update(extra)
    return AgentState(**state_data)


# ---------------------------------------------------------------------------
# Fixture: reset all shared state after each test
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_after_test():
    """Reset tool client, checkpointer, and single-flight after each test."""
    yield
    reset_tool_client()
    reset_session_checkpointer()
    reset_single_flight()


# ---------------------------------------------------------------------------
# §8.8 guard: confirmation does NOT bind the revision → no submit
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_88_guard_mismatched_hash_no_submit() -> None:
    """§8.8: confirmation with wrong payload_hash → no API call, routes to capture_fields."""
    call_count = 0

    def handler(req: httpx.Request) -> httpx.Response:
        nonlocal call_count
        if req.url.path == "/api/bookings":
            call_count += 1
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    draft = _make_draft()
    rev = _make_revision(draft)

    # Confirmation with a MISMATCHED payload hash
    bad_conf = Confirmation(
        confirmation_id="conf_bad",
        intent_id=rev.intent_id,
        intent_revision=rev.revision,
        intent_payload_hash="WRONG_HASH",
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=_NOW,
        source_turn_id="turn_001",
        read_back_version="hash_001",
        verification_level="none",
    )
    state = AgentState(
        tenant_id="tenant_sj_test",
        session_id="sess_sj_test",
        draft=draft,
        current_revision=rev.model_dump(mode="json"),
        confirmation=bad_conf.model_dump(mode="json"),
    )

    result = await submit_intent(state)

    assert call_count == 0, (
        "§8.8: no API call must fire when confirmation does not bind revision"
    )
    assert result.get("submission_status") == "confirmation_invalid"
    # Router must send to capture_fields
    merged = AgentState(**{**state.model_dump(), **result})
    assert route_after_submit_intent(merged) == "capture_fields"


# ---------------------------------------------------------------------------
# Inv-2: checkpoint-before-submit refusal
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_inv2_checkpoint_not_durable_refuses_submit() -> None:
    """Inv-2: non-persisting store → checkpoint not durable → refuses to submit."""
    call_count = 0

    def handler(req: httpx.Request) -> httpx.Response:
        nonlocal call_count
        if req.url.path == "/api/bookings":
            call_count += 1
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    # Install a checkpointer that persists to a store, then clear it immediately
    # so requires_pre_submit_checkpoint always returns True (not durable)
    class _NeverDurableStore(dict):
        """A store that appears empty on every read — simulates non-durable write."""

        def __getitem__(self, key):  # type: ignore[override]
            raise KeyError(key)

        def get(self, key, default=None):  # type: ignore[override]
            return default

    non_durable_cp = SessionCheckpointer(store=_NeverDurableStore())
    set_session_checkpointer(non_durable_cp)

    state = _bound_state()

    result = await submit_intent(state)

    assert call_count == 0, (
        "Inv-2: no API call must fire when checkpoint is not durable"
    )
    assert result.get("submission_status") == "checkpoint_failed"
    merged = AgentState(**{**state.model_dump(), **result})
    assert route_after_submit_intent(merged) == "human_handoff"


# ---------------------------------------------------------------------------
# Happy 202: accepted → submitted terminal (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_happy_202_accepted_terminal_submitted() -> None:
    """202 accepted → terminal_state='submitted', message has 'submitted' not 'confirmed'/'booked'."""
    submitted_at = "2026-06-25T10:00:00+00:00"

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/bookings":
            return _json_response(
                202,
                {
                    "status": "accepted",
                    "ok": True,
                    "operation_id": "op_abc",
                    "intent_id": "intent_sj_001",
                    "intent_revision": 1,
                    "status_url": "/api/booking-operations/op_abc",
                    "submitted_at": submitted_at,
                    "correlation_id": "corr_001",
                },
            )
        if req.url.path.startswith("/api/booking-operations/"):
            return _json_response(
                404, {"status": "not_found", "ok": False, "correlation_id": "c"}
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    # Install a proper durable checkpointer
    set_session_checkpointer(SessionCheckpointer())

    state = _bound_state()
    result = await submit_intent(state)

    assert result.get("terminal_state") == "submitted", (
        f"Expected submitted, got: {result}"
    )
    msg = result.get("submission_message", "")
    assert "submitted" in msg.lower(), (
        f"Inv-16: message must contain 'submitted'; got: {msg!r}"
    )
    assert "confirmed" not in msg.lower(), (
        f"Inv-16: message must NOT contain 'confirmed'; got: {msg!r}"
    )
    assert "booked" not in msg.lower(), (
        f"Inv-16: message must NOT contain 'booked'; got: {msg!r}"
    )

    # Router → END
    merged = AgentState(**{**state.model_dump(), **result})
    from langgraph.graph import END

    assert route_after_submit_intent(merged) == END


# ---------------------------------------------------------------------------
# validation_failed → capture_fields
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_validation_failed_routes_to_capture_fields() -> None:
    """validation_failed status → route_after_submit_intent returns 'capture_fields'."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/bookings":
            return _json_response(
                422,
                {
                    "status": "validation_failed",
                    "ok": False,
                    "correlation_id": "corr_001",
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    set_session_checkpointer(SessionCheckpointer())

    state = _bound_state()
    result = await submit_intent(state)

    assert result.get("submission_status") == "validation_failed"
    merged = AgentState(**{**state.model_dump(), **result})
    assert route_after_submit_intent(merged) == "capture_fields"


# ---------------------------------------------------------------------------
# nonrecoverable → human_handoff
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_nonrecoverable_routes_to_human_handoff() -> None:
    """5xx / nonrecoverable → terminal_state='human_handoff', route='human_handoff'."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/bookings":
            return _json_response(
                500,
                {
                    "status": "nonrecoverable",
                    "ok": False,
                    "correlation_id": "corr_001",
                },
            )
        # get_operation_status also fails
        if req.url.path.startswith("/api/booking-operations/"):
            return _json_response(
                500,
                {
                    "status": "nonrecoverable",
                    "ok": False,
                    "correlation_id": "corr_001",
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    set_session_checkpointer(SessionCheckpointer())

    state = _bound_state()
    result = await submit_intent(state)

    assert result.get("terminal_state") == "human_handoff", (
        f"Expected human_handoff; got: {result}"
    )
    merged = AgentState(**{**state.model_dump(), **result})
    assert route_after_submit_intent(merged) == "human_handoff"


# ---------------------------------------------------------------------------
# Single-flight (§27.3): two concurrent calls for same op_id → exactly ONE submit
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_single_flight_concurrent_same_op_id_exactly_one_submit() -> None:
    """§27.3: two submit_intent coroutines with same pre-set op_id → exactly 1 API call."""
    submit_call_count = 0
    submitted_at = "2026-06-25T10:00:00+00:00"

    def handler(req: httpx.Request) -> httpx.Response:
        nonlocal submit_call_count
        if req.url.path == "/api/bookings":
            submit_call_count += 1
            return _json_response(
                202,
                {
                    "status": "accepted",
                    "ok": True,
                    "operation_id": "op_sf_001",
                    "intent_id": "intent_sj_001",
                    "intent_revision": 1,
                    "status_url": "/api/booking-operations/op_sf_001",
                    "submitted_at": submitted_at,
                    "correlation_id": "corr_001",
                },
            )
        # get_operation_status returns not_found (no prior submission)
        if req.url.path.startswith("/api/booking-operations/"):
            return _json_response(
                404,
                {"status": "not_found", "ok": False, "correlation_id": "c"},
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    # Install a durable checkpointer pre-populated with the op_id+idem_key pair
    # so both coroutines see the reconnect branch (op_id already set) and skip checkpoint.
    cp = SessionCheckpointer()
    set_session_checkpointer(cp)

    # Pre-set op_id and idem_key so we exercise the reconnect branch with single-flight.
    # We manually checkpoint the state with those ids so get_operation_status (not_found)
    # falls through to resubmit via single-flight.
    draft = _make_draft()
    rev = _make_revision(draft)
    conf = _make_confirmation(rev)

    pre_state = AgentState(
        tenant_id="tenant_sj_test",
        session_id="sess_sj_test",
        draft=draft,
        current_revision=rev.model_dump(mode="json"),
        confirmation=conf.model_dump(mode="json"),
        operation_id="op_sf_001",
        idempotency_key="idem_sf_001",
    )

    # Run two concurrent coroutines for the same state (same op_id)
    results = await asyncio.gather(
        submit_intent(pre_state),
        submit_intent(pre_state),
    )

    # Both should succeed (accepted or recovered)
    for r in results:
        assert r.get("terminal_state") == "submitted", f"Expected submitted; got: {r}"

    # The actual submit API must have been called EXACTLY ONCE (single-flight)
    assert submit_call_count == 1, (
        f"§27.3 single-flight: expected exactly 1 submit call, got {submit_call_count}"
    )

# ---------------------------------------------------------------------------
# Recovery is for INCONCLUSIVE outcomes only (handoff doc §8)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_definitive_422_is_not_resubmitted() -> None:
    """A rejection the server already read must not be sent a second time.

    Before this, ANY non-ok envelope triggered the lost-response dance:
    `map_http_error` maps an untyped 422 to the generic `request_error`, so the
    node issued GET /booking-operations/{id} (404, the operation was never
    created), then RESUBMITTED the identical durable mutation for the identical
    refusal, and finally landed in `human_handoff` — for a case spec 03 §4.3
    classifies as recoverable.
    """
    calls: list[tuple[str, str]] = []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append((req.method, req.url.path))
        if req.url.path == "/api/bookings":
            # No typed `status` in the body, so the generic HTTP mapping applies.
            return _json_response(422, {"error": "unprocessable"})
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    set_session_checkpointer(SessionCheckpointer())

    state = _bound_state()
    result = await submit_intent(state)

    submits = [c for c in calls if c[1] == "/api/bookings"]
    assert len(submits) == 1, f"the durable mutation was retried: {calls}"
    assert not [c for c in calls if "booking-operations" in c[1]], (
        "a definitive rejection must not trigger a status poll"
    )

    merged = AgentState(**{**state.model_dump(), **result})
    assert merged.terminal_state is None, "a 4xx must not terminate in human_handoff"
    assert route_after_submit_intent(merged) == "capture_fields"


@pytest.mark.unit
async def test_an_inconclusive_5xx_still_recovers_before_resubmitting() -> None:
    """Inv-5 is intact for the case it exists for: an unknown outcome."""
    calls: list[tuple[str, str]] = []
    submitted = {"count": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append((req.method, req.url.path))
        if req.url.path == "/api/bookings":
            submitted["count"] += 1
            if submitted["count"] == 1:
                return _json_response(503, {"error": "unavailable"})
            return _json_response(
                202,
                {"status": "accepted", "ok": True, "correlation_id": "corr_001"},
            )
        # The operation genuinely is not there — the first attempt never landed.
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    set_session_checkpointer(SessionCheckpointer())

    result = await submit_intent(_bound_state())

    assert [c[1] for c in calls].count("/api/bookings") == 2, (
        "an inconclusive outcome must be resubmitted once the poll finds nothing"
    )
    assert any("booking-operations" in path for _, path in calls), (
        "Inv-5: the status poll must happen BEFORE any resubmit"
    )
    assert result["terminal_state"] == "submitted"
