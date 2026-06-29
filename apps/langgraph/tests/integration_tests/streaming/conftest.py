"""Shared fixtures and helpers for the 01-I e2e streaming contract tests.

Wires the REAL spec-02 agent components with the REAL streaming components so
the three invariant suites (Inv-1, Inv-16, Inv-2) run against actual production
code rather than fakes.

Guard: ``requires_spec02()`` skips the calling test when agent-graph imports
fail so that ticket-01 remains independently mergeable.  Since spec-02 IS
available, tests actually run -- the guard is defensive only.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime
from typing import Any

import httpx
import pytest

from cordia_agent.checkpoint import (
    SessionCheckpointer,
    reset_session_checkpointer,
    set_session_checkpointer,
)
from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.recovery import reset_single_flight
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.capture import clear_slot_extractor
from cordia_agent.state import Channel, StreamFrameType
from cordia_agent.streaming.reconnect import ReconnectSessionContext  # noqa: TC001
from cordia_agent.streaming_models import (
    BargeInFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TurnCancelledFrame,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.client_binding import get_operation_status_call


# ---------------------------------------------------------------------------
# Spec-02 guard
# ---------------------------------------------------------------------------


def requires_spec02() -> None:
    """Skip the calling test if spec-02 agent graph/components cannot be imported."""
    try:
        from cordia_agent.graph import build_agent_graph  # noqa: F401
        from cordia_agent.nodes.await_confirmation import (
            await_confirmation,  # noqa: F401
        )
        from cordia_agent.nodes.capture_fields import capture_fields  # noqa: F401
    except ImportError as exc:
        pytest.skip(f"spec-02 agent components unavailable: {exc}")


# ---------------------------------------------------------------------------
# Shared session identity envelope
# ---------------------------------------------------------------------------

TENANT_ID = "tenant_e2e_01i"
SESSION_ID = "sess_e2e_01i"
TURN_ID = "turn_e2e_01i"
CORRELATION_ID = "corr_e2e_01i"
CHANNEL_REF = "ch_e2e_01i"

_TS = datetime(2026, 6, 25, 10, 0, 0, tzinfo=UTC)

_ENVELOPE: dict[str, Any] = {
    "tenant_id": TENANT_ID,
    "session_id": SESSION_ID,
    "turn_id": TURN_ID,
    "correlation_id": CORRELATION_ID,
    "causation_id": None,
    "ts": _TS.isoformat(),
}


# ---------------------------------------------------------------------------
# Frame builders
# ---------------------------------------------------------------------------


def make_partial_frame(
    text: str = "I want to book", *, seq: int = 0
) -> TranscriptPartialFrame:
    """Build a TranscriptPartialFrame with the shared session envelope."""
    return TranscriptPartialFrame(
        type=StreamFrameType.TRANSCRIPT_PARTIAL,
        **_ENVELOPE,
        seq=seq,
        text=text,
        stability=0.7,
    )


def make_final_frame(
    text: str = "I want to book a haircut", *, seq: int = 1
) -> TranscriptFinalFrame:
    """Build a TranscriptFinalFrame with the shared session envelope."""
    return TranscriptFinalFrame(
        type=StreamFrameType.TRANSCRIPT_FINAL,
        **_ENVELOPE,
        seq=seq,
        text=text,
        asr_confidence=0.95,
        is_endpoint=True,
    )


def make_barge_in_frame(*, seq: int = 2) -> BargeInFrame:
    """Build a BargeInFrame with the shared session envelope."""
    return BargeInFrame(
        type=StreamFrameType.BARGE_IN_DETECTED,
        **_ENVELOPE,
        seq=seq,
    )


def make_turn_cancelled_frame(
    cancelled_turn_id: str,
    *,
    seq: int = 3,
    material_content_delivered: bool = False,
) -> TurnCancelledFrame:
    """Build a TurnCancelledFrame with the shared session envelope."""
    return TurnCancelledFrame(
        type=StreamFrameType.TURN_CANCELLED,
        **_ENVELOPE,
        seq=seq,
        cancelled_turn_id=cancelled_turn_id,
        material_content_delivered=material_content_delivered,
    )


# ---------------------------------------------------------------------------
# Fake 04 API (MockTransport)
# ---------------------------------------------------------------------------

_KNOWN_OP_ID = "op_e2e_01i_known"
_KNOWN_IDEM_KEY = "idem_e2e_01i_known"
_SUBMITTED_AT = "2026-06-25T10:00:00+00:00"
_UPDATED_AT = "2026-06-25T10:01:00+00:00"


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


class FakeE2EApiHandler:
    """Stateful fake 04 API for the 01-I e2e suite.

    Serves:
    - GET /api/booking-operations/{op_id}: returns a NormalizedOperationStatus-shaped
      OperationStatusView for the KNOWN op_id; 404 otherwise.
    - POST /api/bookings: records the submit and returns 202 accepted.

    ``submit_count`` is the number of POST /api/bookings calls received.
    """

    def __init__(self) -> None:
        self.submit_count: int = 0
        self.requests: list[httpx.Request] = []

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        method = request.method.upper()
        path = request.url.path

        if method == "GET" and path.startswith("/api/booking-operations/"):
            queried_op_id = path.split("/")[-1]
            if queried_op_id == _KNOWN_OP_ID:
                return _json_response(
                    200,
                    {
                        "status": "accepted",
                        "ok": True,
                        "operation_id": _KNOWN_OP_ID,
                        "booking_id": None,
                        "booking_reference": None,
                        "availability_mode": None,
                        "customer_message": "Your request has been submitted.",
                        "pending": True,
                        "terminal": False,
                        "updated_at": _UPDATED_AT,
                        "correlation_id": CORRELATION_ID,
                    },
                )
            return _json_response(
                404,
                {"status": "not_found", "ok": False, "correlation_id": CORRELATION_ID},
            )

        if method == "POST" and path == "/api/bookings":
            self.submit_count += 1
            body_bytes = request.content
            try:
                body = json.loads(body_bytes)
            except Exception:
                body = {}
            op_id = body.get("operation_id", _KNOWN_OP_ID)
            return _json_response(
                202,
                {
                    "status": "accepted",
                    "ok": True,
                    "operation_id": op_id,
                    "intent_id": body.get("intent_id", "intent_e2e_001"),
                    "intent_revision": body.get("intent_revision", 1),
                    "status_url": f"/api/booking-operations/{op_id}",
                    "submitted_at": _SUBMITTED_AT,
                    "correlation_id": CORRELATION_ID,
                },
            )

        # get_tenant_context -- needed so tools don't hit the Inv-13 gate
        if method == "GET" and path == "/api/tenant-context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "tenant_id": TENANT_ID,
                    "policy_version": "v1",
                    "timezone": "Australia/Sydney",
                    "supported_operations": ["create"],
                    "capabilities": [],
                    "currency": "AUD",
                },
            )

        return _json_response(
            404,
            {"status": "not_found", "ok": False, "correlation_id": CORRELATION_ID},
        )


# ---------------------------------------------------------------------------
# TenantContext for the e2e suite
# ---------------------------------------------------------------------------

FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id=TENANT_ID,
    channel=Channel.voice,
    channel_endpoint_id="ep_e2e_01i",
    configuration_version="v1",
    correlation_id=CORRELATION_ID,
    allowed_policy_ids=["policy_a"],
)


# ---------------------------------------------------------------------------
# SpecTwoAgentResumeHook
# ---------------------------------------------------------------------------


class SpecTwoAgentResumeHook:
    """Real AgentResumeHook backed by SessionCheckpointer + get_operation_status_call.

    Implements the AgentResumeHook protocol:
    - Loads the AgentState from the SessionCheckpointer (by tenant_id/session_id).
    - Reads the operation_id from the state.
    - Calls get_operation_status_call(operation_id) and returns the recovered status.
    - Never mints a new operation_id.

    ``recovered_op_id`` is set after a successful resume so tests can assert it.
    """

    def __init__(self, checkpointer: SessionCheckpointer) -> None:
        self._checkpointer = checkpointer
        self.recovered_op_id: str | None = None
        self.resume_context: ReconnectSessionContext | None = None

    async def resume(
        self, context: ReconnectSessionContext
    ) -> NormalizedOperationStatus | None:
        self.resume_context = context

        state = self._checkpointer.load(context.tenant_id, context.session_id)
        if state is None or state.operation_id is None:
            return None

        self.recovered_op_id = state.operation_id

        env = await get_operation_status_call(state.operation_id)
        if not env.ok or env.data is None:
            return None

        # Map the status string to NormalizedOperationStatus
        try:
            return NormalizedOperationStatus(env.data.get("status", ""))
        except ValueError:
            return None


# ---------------------------------------------------------------------------
# Autouse fixture: set up and tear down shared module singletons
# ---------------------------------------------------------------------------


@pytest.fixture()
def fake_api_handler() -> FakeE2EApiHandler:
    return FakeE2EApiHandler()


@pytest.fixture()
def tool_client(fake_api_handler: FakeE2EApiHandler) -> ToolClient:
    transport = httpx.MockTransport(fake_api_handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=FAKE_TENANT_CONTEXT)


@pytest.fixture(autouse=True)
def _reset_singletons(tool_client: ToolClient) -> None:
    """Wire the ToolClient and reset all singletons before/after each test."""
    set_session_checkpointer(SessionCheckpointer())
    set_tool_client(tool_client)
    yield
    reset_tool_client()
    reset_session_checkpointer()
    reset_single_flight()
    clear_slot_extractor()
