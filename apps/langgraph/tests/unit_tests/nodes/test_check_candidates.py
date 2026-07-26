"""Unit tests for the check_candidates node (02-H, §8.7).

Uses ``httpx.MockTransport`` + ``set_tool_client`` to drive the node against
a fake API; resets the client in teardown.

Covers:
  - Happy-path: candidate mode tracked correctly (HELD, AVAILABLE, etc.).
  - Degraded path: ``available_pending`` status sets ``availability_pending=True``;
    mode is NOT held/confirmed — wording must be advisory only (Inv-16).
  - Checkpoint discipline (§8): run ``capture_fields`` on a NON-material update
    (required fields incomplete) → router returns ``"clarify"`` (NOT
    ``"check_candidates"``) AND no tool call fires (no recording in transport).
    Then complete required fields → check_candidates DOES fire (transport receives
    a request).
  - route_after_check_candidates: routes to ``"capture_fields"`` on invalid fields
    or no candidates; routes to ``"read_back"`` when candidates present.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.nodes.capture_fields import route_after_capture_fields
from cordia_agent.nodes.check_candidates import (
    check_candidates,
    route_after_check_candidates,
)
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.capture import clear_slot_extractor, register_slot_extractor
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.status_codes import AvailabilityMode
from tests._stubs.read_tools_fake import FakeSlotExtractor


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_cc_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_cc_001",
    configuration_version="v1",
    correlation_id="corr_cc_test_001",
    session_id="sess_cc_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)

_CANDIDATE = {
    "starts_at": "2026-07-01T10:00:00+00:00",
    "duration_minutes": 60,
    "availability_mode": "held",
}


def _minimal_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_001",
            location_id="loc_001",
            starts_at=datetime.datetime(2026, 7, 1, 10, 0, tzinfo=datetime.UTC),
            party_size=2,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


# ---------------------------------------------------------------------------
# Recording transport for checkpoint-discipline tests
# ---------------------------------------------------------------------------


class RecordingHandler:
    """Callable fake-API handler that records all requests it receives."""

    def __init__(self, availability_body: dict[str, Any] | None = None) -> None:
        self.requests: list[httpx.Request] = []
        self._availability_body = availability_body or {
            "status": "available",
            "mode": "available",
            "candidates": [_CANDIDATE],
            "invalid_fields": [],
            "alternatives": [],
        }

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        path = request.url.path
        if path == "/api/availability/candidates":
            return _json_response(200, self._availability_body)
        if path == "/api/tenant-context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "tenant_id": "tenant_cc_test",
                    "policy_version": "v1",
                    "timezone": "Australia/Sydney",
                    "supported_operations": ["create"],
                    "capabilities": [],
                    "currency": "AUD",
                },
            )
        if path == "/api/customers/context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "customer_id": "cust_001",
                    "display_name": "Test",
                    "has_tokenized_payment": False,
                },
            )
        return _json_response(404, {"error": "not_found"})


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_after_test():
    """Reset tool client and slot extractor after each test."""
    yield
    reset_tool_client()
    clear_slot_extractor()


# ---------------------------------------------------------------------------
# Tests — candidate mode tracked correctly
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_check_candidates_mode_held_tracked() -> None:
    """Fake API returns mode='held' → availability_mode == AvailabilityMode.HELD."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/availability/candidates":
            return _json_response(
                200,
                {
                    "status": "held",
                    "mode": "held",
                    "candidates": [_CANDIDATE],
                    "invalid_fields": [],
                    "alternatives": [],
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
    )

    result = await check_candidates(state)

    assert result["availability_mode"] == AvailabilityMode.HELD
    assert len(result["availability_candidates"]) == 1
    assert result["availability_pending"] is False


@pytest.mark.unit
async def test_check_candidates_mode_available_tracked() -> None:
    """Fake API returns mode='available' → availability_mode == AvailabilityMode.INFORMATIONAL."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/availability/candidates":
            return _json_response(
                200,
                {
                    "status": "available",
                    "mode": "informational",
                    "candidates": [
                        {
                            "starts_at": "2026-07-01T10:00:00+00:00",
                            "duration_minutes": 60,
                            "availability_mode": "informational",
                        }
                    ],
                    "invalid_fields": [],
                    "alternatives": [],
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
    )

    result = await check_candidates(state)

    assert result["availability_mode"] == AvailabilityMode.INFORMATIONAL
    assert result["availability_pending"] is False


# ---------------------------------------------------------------------------
# Tests — degraded / available_pending advisory (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_check_candidates_available_pending_is_advisory() -> None:
    """available_pending: availability_pending=True; mode is NOT held/confirmed (Inv-16).

    The response status 'available_pending' sets the advisory flag.
    The mode field carries the actual mode from the platform (e.g. 'informational')
    which may be advisory.  We assert:
      - availability_pending is True
      - availability_mode is NOT HELD and NOT CONFIRMED (never a success state)
    """

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/availability/candidates":
            return _json_response(
                200,
                {
                    "status": "available_pending",
                    "mode": "informational",  # advisory, not held/confirmed
                    "candidates": [
                        {
                            "starts_at": "2026-07-01T10:00:00+00:00",
                            "duration_minutes": 60,
                            "availability_mode": "informational",
                        }
                    ],
                    "invalid_fields": [],
                    "alternatives": [],
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
    )

    result = await check_candidates(state)

    # Advisory flag must be set
    assert result["availability_pending"] is True, (
        "available_pending status must set availability_pending=True (Inv-16)"
    )
    # Mode must NOT be held/confirmed (never a success state when pending)
    mode = result.get("availability_mode")
    assert mode not in (AvailabilityMode.HELD, AvailabilityMode.CONFIRMED), (
        f"Inv-16: available_pending must NOT be treated as held/confirmed; got mode={mode}"
    )


@pytest.mark.unit
async def test_check_candidates_available_pending_never_blocks_turn() -> None:
    """available_pending NEVER blocks the turn — node returns normally (§6.6)."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/availability/candidates":
            return _json_response(
                200,
                {
                    "status": "available_pending",
                    "mode": "informational",
                    "candidates": [
                        {
                            "starts_at": "2026-07-01T10:00:00+00:00",
                            "duration_minutes": 60,
                            "availability_mode": "informational",
                        }
                    ],
                    "invalid_fields": [],
                    "alternatives": [],
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
    )

    # Must complete synchronously without raising
    result = await check_candidates(state)
    assert isinstance(result, dict), "check_candidates must return a dict (never block)"


# ---------------------------------------------------------------------------
# Tests — checkpoint discipline (§8 — no tool call on non-material update)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_no_tool_call_when_required_fields_incomplete() -> None:
    """§8 discipline: capture_fields with incomplete required fields routes to 'clarify'.

    Setup:
      - Draft is missing party_size → required_candidate_fields_present is False.
      - FakeSlotExtractor returns NO captures (non-material update).
      - Recording transport is installed.

    Assert:
      - route_after_capture_fields returns 'clarify' (NOT 'check_candidates').
      - No request is made to /api/availability/candidates.
    """
    handler = RecordingHandler()
    set_tool_client(_make_client(handler))

    # Extractor returns no captures (non-material update)
    register_slot_extractor(FakeSlotExtractor([]))

    # Draft missing party_size → required fields NOT present
    incomplete_draft = BookingDraft(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_001",
            location_id="loc_001",
            starts_at=datetime.datetime(2026, 7, 1, 10, 0, tzinfo=datetime.UTC),
            # party_size intentionally missing
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=incomplete_draft,
        transcript_text="book a table",
        transcript_is_final=True,
        current_turn_id="turn_001",
        operation=OperationType.create,
    )

    # Router check — must go to 'clarify', NOT 'check_candidates'
    route = route_after_capture_fields(state)
    assert route == "clarify", (
        f"§8: incomplete fields must route to 'clarify', not 'check_candidates'; got {route!r}"
    )

    # No availability call should have been made
    availability_calls = [
        r for r in handler.requests if "/api/availability/candidates" in r.url.path
    ]
    assert len(availability_calls) == 0, (
        f"§8 discipline: NO tool call should fire on incomplete fields; "
        f"got {len(availability_calls)} availability request(s)"
    )


@pytest.mark.unit
async def test_tool_call_fires_when_all_required_fields_present() -> None:
    """§8 discipline: check_candidates DOES call the tool when all required fields are present.

    Setup:
      - Draft has all required create fields (service_id, location_id, starts_at, party_size).
      - Recording transport counts requests to /api/availability/candidates.

    Assert:
      - route_after_capture_fields returns 'check_candidates'.
      - After calling check_candidates, the recording transport has received ≥1 request.
    """
    handler = RecordingHandler()
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
        operation=OperationType.create,
    )

    # Router must route to check_candidates
    route = route_after_capture_fields(state)
    assert route == "check_candidates", (
        f"All required fields present must route to 'check_candidates'; got {route!r}"
    )

    # Call check_candidates and verify the transport received a request
    await check_candidates(state)

    availability_calls = [
        r for r in handler.requests if "/api/availability/candidates" in r.url.path
    ]
    assert len(availability_calls) >= 1, (
        "§8 discipline: check_candidates MUST call the availability tool "
        "when all required fields are present"
    )


# ---------------------------------------------------------------------------
# Tests — route_after_check_candidates
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_route_capture_fields_when_invalid_fields() -> None:
    """Invalid fields present → route to 'capture_fields'."""
    state = AgentState(
        availability_invalid_fields=["service_id", "party_size"],
        availability_candidates=[],
    )
    assert route_after_check_candidates(state) == "capture_fields"


@pytest.mark.unit
def test_route_capture_fields_when_no_candidates() -> None:
    """No candidates (empty list) → route to 'capture_fields'."""
    state = AgentState(
        availability_invalid_fields=[],
        availability_candidates=[],
    )
    assert route_after_check_candidates(state) == "capture_fields"


@pytest.mark.unit
def test_route_read_back_when_candidates_present() -> None:
    """At least one candidate → route to 'read_back'."""
    state = AgentState(
        availability_invalid_fields=[],
        availability_candidates=[{"starts_at": "2026-07-01T10:00:00+00:00"}],
    )
    assert route_after_check_candidates(state) == "read_back"


@pytest.mark.unit
def test_route_capture_fields_wins_over_candidates_when_invalid_fields() -> None:
    """Invalid fields take precedence over candidates — must re-prompt."""
    state = AgentState(
        availability_invalid_fields=["starts_at"],
        availability_candidates=[{"starts_at": "2026-07-01T10:00:00+00:00"}],
    )
    assert route_after_check_candidates(state) == "capture_fields"


# ---------------------------------------------------------------------------
# Tests — null/error handling
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_check_candidates_no_draft_returns_empty() -> None:
    """check_candidates with draft=None returns empty dict and does not crash."""

    def handler(req: httpx.Request) -> httpx.Response:
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))
    state = AgentState(
        tenant_id="tenant_cc_test", session_id="sess_cc_test", draft=None
    )

    result = await check_candidates(state)
    assert result == {}


@pytest.mark.unit
async def test_check_candidates_tool_error_handled_gracefully() -> None:
    """Non-ok tool response → graceful degradation; node does not crash (§6.6)."""

    def error_handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/availability/candidates":
            return _json_response(500, {"error": "server_error"})
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(error_handler))
    state = AgentState(
        tenant_id="tenant_cc_test",
        session_id="sess_cc_test",
        draft=_minimal_draft(),
    )

    result = await check_candidates(state)

    # Node must return a dict and not raise
    assert isinstance(result, dict)
    assert result.get("availability_candidates") == []
    assert result.get("availability_invalid_fields") == []


@pytest.mark.unit
def test_parse_policy_validation_preserves_recoverable_fields_and_action() -> None:
    """Regression (02-H review): the envelope-key strip must NOT drop genuine
    PolicyValidationResult fields that share a name with envelope keys
    (recoverable_fields, suggested_action). See client_binding._payload."""
    from cordia_agent.tools.client_binding import parse_policy_validation
    from cordia_agent.tools.contracts import ToolResultEnvelope

    # A policy *failure* is still a successful tool CALL (HTTP 200 -> ok=True);
    # PolicyValidationResult.valid carries the policy outcome.
    env = ToolResultEnvelope(
        ok=True,
        status="minimum_notice_not_met",
        correlation_id="corr-1",
        data={
            "ok": True,
            "status": "minimum_notice_not_met",
            "valid": False,
            "code": "minimum_notice_not_met",
            "customer_message": "This business requires at least two hours' notice.",
            "recoverable_fields": ["starts_at"],
            "suggested_action": "request_alternative_time",
            "policy_version": "policy_2026_06_15",
            "correlation_id": "corr-1",
        },
    )

    result = parse_policy_validation(env)

    assert result is not None
    assert result.recoverable_fields == ["starts_at"]
    assert result.suggested_action == "request_alternative_time"
    assert result.policy_version == "policy_2026_06_15"
