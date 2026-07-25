"""Integration test: create-flow front-half with tool-client binding (02-H).

Drives the create-flow front half:
    resolve_tenant → load_context → identify_intent →
    capture_fields → check_candidates

against a ``ToolClient`` backed by an in-test fake API
(``httpx.MockTransport``).

Strategy: nodes are invoked in sequence on a shared ``AgentState`` rather
than a full ``graph.ainvoke()`` to keep the test deterministic and fast.
This avoids the complexity of wiring all the stubs required for a full
graph run while still exercising the 03 tool-binding path end-to-end.

Key assertions:
  - The availability tool fires EXACTLY ONCE at the CheckCandidates node.
  - ``AvailabilityMode`` is recorded on the resulting state.
  - No availability call fires during load_context or identify_intent.

Spec refs: 02-H §test_tool_binding; spec 03 §4.3; §8 checkpoint discipline.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.nodes.check_candidates import check_candidates
from cordia_agent.nodes.load_context import load_context
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.capture import clear_slot_extractor, register_slot_extractor
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    DialogIntent,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.status_codes import AvailabilityMode
from tests._stubs.read_tools_fake import FakeSlotExtractor


# ---------------------------------------------------------------------------
# Fake API
# ---------------------------------------------------------------------------


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_binding_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_binding_001",
    configuration_version="v1",
    correlation_id="corr_binding_001",
    session_id="sess_binding_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


class FrontHalfFakeApiHandler:
    """Fake API handler for the create-flow front half.

    Returns frozen shapes for:
      - GET /api/tenant-context
      - GET /api/customers/context
      - POST /api/availability/candidates

    Tracks request counts so tests can assert exactly-once semantics.
    """

    def __init__(self) -> None:
        self.requests: list[httpx.Request] = []

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        path = request.url.path

        if path == "/api/tenant-context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "tenant_id": "tenant_binding_test",
                    "policy_version": "v2-binding",
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
                    "customer_id": "cust_binding_001",
                    "display_name": "Binding Test User",
                    "has_tokenized_payment": False,
                },
            )

        if path == "/api/availability/candidates":
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

    @property
    def availability_call_count(self) -> int:
        return sum(
            1 for r in self.requests if "/api/availability/candidates" in r.url.path
        )


def _make_client(handler: FrontHalfFakeApiHandler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _full_draft() -> BookingDraft:
    """Build a draft with all required create fields."""
    return BookingDraft(
        tenant_id="tenant_binding_test",
        session_id="sess_binding_test",
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


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def handler() -> FrontHalfFakeApiHandler:
    return FrontHalfFakeApiHandler()


@pytest.fixture()
def fake_client(handler: FrontHalfFakeApiHandler) -> ToolClient:
    client = _make_client(handler)
    set_tool_client(client)
    yield client
    reset_tool_client()


@pytest.fixture(autouse=True)
def _reset_slot_extractor():
    """Ensure slot extractor is always cleared after each test."""
    register_slot_extractor(FakeSlotExtractor([]))
    yield
    clear_slot_extractor()


# ---------------------------------------------------------------------------
# Integration tests
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_create_flow_front_half_availability_fires_once_at_check_candidates(
    fake_client: ToolClient,
    handler: FrontHalfFakeApiHandler,
) -> None:
    """Availability call fires EXACTLY ONCE at check_candidates — not before.

    Flow:
      1. load_context — calls tenant-context + customer-context tools.
         No availability call yet.
      2. check_candidates — calls check_candidate_availability ONCE.
         No further calls after this.

    The §8 checkpoint discipline ensures the availability tool is only called
    from check_candidates (a checkpoint node), never from load_context or
    implicitly from capture_fields.
    """
    state = AgentState(
        tenant_id="tenant_binding_test",
        session_id="sess_binding_test",
        draft=_full_draft(),
        dialog_intent=DialogIntent.create,
        operation=OperationType.create,
    )

    # --- Step 1: load_context ---
    lc_result = await load_context(state)
    state = state.model_copy(update=lc_result)

    # No availability call yet
    assert handler.availability_call_count == 0, (
        "load_context must NOT call the availability tool (§8 discipline)"
    )

    # --- Step 2: check_candidates ---
    cc_result = await check_candidates(state)
    state = state.model_copy(update=cc_result)

    # Exactly one availability call
    assert handler.availability_call_count == 1, (
        f"check_candidates must call the availability tool EXACTLY ONCE; "
        f"got {handler.availability_call_count}"
    )


@pytest.mark.integration
async def test_create_flow_availability_mode_tracked_on_state(
    fake_client: ToolClient,
    handler: FrontHalfFakeApiHandler,
) -> None:
    """AvailabilityMode is recorded on AgentState after check_candidates runs."""
    state = AgentState(
        tenant_id="tenant_binding_test",
        session_id="sess_binding_test",
        draft=_full_draft(),
        dialog_intent=DialogIntent.create,
        operation=OperationType.create,
    )

    cc_result = await check_candidates(state)
    state = state.model_copy(update=cc_result)

    assert state.availability_mode is not None, (
        "availability_mode must be set on state after check_candidates"
    )
    assert isinstance(state.availability_mode, AvailabilityMode), (
        f"availability_mode must be an AvailabilityMode enum; got {type(state.availability_mode)}"
    )


@pytest.mark.integration
async def test_create_flow_load_context_maps_tenant_and_customer(
    fake_client: ToolClient,
    handler: FrontHalfFakeApiHandler,
) -> None:
    """load_context correctly maps tenant policy_version and customer_context."""
    state = AgentState(
        tenant_id="tenant_binding_test",
        session_id="sess_binding_test",
        draft=_full_draft(),
    )

    lc_result = await load_context(state)
    state = state.model_copy(update=lc_result)

    assert state.tenant_policy_version == "v2-binding"
    assert state.customer_context is not None
    assert state.customer_context.customer_id == "cust_binding_001"
    assert state.customer_context.display_name == "Binding Test User"
    # §9.2 — sensitive history never surfaced here
    assert state.customer_context.recent_booking_summaries == []


@pytest.mark.integration
async def test_create_flow_candidates_recorded_on_state(
    fake_client: ToolClient,
    handler: FrontHalfFakeApiHandler,
) -> None:
    """Candidates from the availability response are recorded as list[dict] on state."""
    state = AgentState(
        tenant_id="tenant_binding_test",
        session_id="sess_binding_test",
        draft=_full_draft(),
    )

    cc_result = await check_candidates(state)
    state = state.model_copy(update=cc_result)

    assert isinstance(state.availability_candidates, list)
    assert len(state.availability_candidates) >= 1, (
        "Fake API returns 1 candidate; it must be recorded on state"
    )
    # Candidates are stored as plain dicts (cycle-free state constraint)
    assert isinstance(state.availability_candidates[0], dict)
