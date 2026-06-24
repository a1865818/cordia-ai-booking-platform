"""Unit tests for the 6 manage-booking tool definitions (spec 03 §4.4, ticket 03-F).

Invariants verified:
- MANAGE_BOOKING_TOOLS has exactly 6 entries with the correct spec names.
- Each tool delegates to EXACTLY ONE client call (inspect t.coroutine source).
- All 6 tools are inspected — vacuous-skip guard (assert inspected count == 6).
- No forbidden imports (httpx, sqlalchemy, psycopg, requests, socket) in manage_booking.py.
- Inv-11 grant gate: submit_booking_modification + submit_booking_cancellation with
  no grant_id return verification_required with zero transport hits (REAL ToolClient
  over recording MockTransport). With a grant_id the transport IS hit and
  X-Grant-Id is set.
- Inv-2: operation_id and idempotency_key flow verbatim through call_tool.
"""

from __future__ import annotations

import inspect
from typing import Any

import httpx
import pytest

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    SubmitCancellationInput,
    SubmitModificationInput,
    ToolResultEnvelope,
)
from cordia_agent.tools.manage_booking import (
    MANAGE_BOOKING_TOOLS,
    find_manageable_bookings,
    get_booking_details,
    get_booking_operation_status,
    submit_booking_cancellation,
    submit_booking_modification,
    validate_booking_change,
)


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

EXPECTED_TOOL_NAMES = {
    "find_manageable_bookings",
    "get_booking_details",
    "validate_booking_change",
    "submit_booking_modification",
    "submit_booking_cancellation",
    "get_booking_operation_status",
}


# ---------------------------------------------------------------------------
# Helpers — minimal valid model instances
# ---------------------------------------------------------------------------


def _minimal_confirmation_dict() -> dict[str, Any]:
    return {
        "confirmation_id": "conf_001",
        "intent_id": "intent_001",
        "intent_revision": 1,
        "intent_payload_hash": "hash_abc",
        "identity": "anonymous_session",
        "confirmation_channel": "voice",
        "confirmed_at": "2026-06-25T10:00:00+00:00",
        "source_turn_id": "turn_001",
        "read_back_version": "v1",
        "verification_level": "none",
    }


def _canned_envelope(status: str = "ok") -> ToolResultEnvelope:
    return ToolResultEnvelope(
        ok=True,
        status=status,
        data=None,
        customer_hint=None,
        correlation_id="corr_test_001",
    )


# ---------------------------------------------------------------------------
# Recording fake client (for delegation / arg passthrough tests)
# ---------------------------------------------------------------------------


class FakeClient:
    """Records call_tool invocations; returns a canned ToolResultEnvelope."""

    def __init__(self, return_value: ToolResultEnvelope | None = None) -> None:
        self._return_value = return_value or _canned_envelope()
        self.calls: list[dict[str, Any]] = []

    async def call_tool(
        self,
        tool_name: str,
        method: str,
        path: str,
        *,
        json: dict[str, Any] | None = None,
        params: dict[str, Any] | None = None,
        operation_id: str | None = None,
        idempotency_key: str | None = None,
        grant_id: str | None = None,
    ) -> ToolResultEnvelope:
        self.calls.append(
            {
                "tool_name": tool_name,
                "method": method,
                "path": path,
                "json": json,
                "params": params,
                "operation_id": operation_id,
                "idempotency_key": idempotency_key,
                "grant_id": grant_id,
            }
        )
        return self._return_value


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def fake_client() -> FakeClient:
    """Register a fresh FakeClient; reset after the test."""
    client = FakeClient()
    set_tool_client(client)
    yield client
    reset_tool_client()


@pytest.fixture()
def resolved_tenant_ctx() -> TenantContext:
    return TenantContext(
        tenant_id="tenant_test_001",
        channel=Channel.voice,
        channel_endpoint_id="ep_test_001",
        configuration_version="v1",
        correlation_id="corr_test_abc",
        allowed_policy_ids=["policy_a"],
    )


@pytest.fixture()
def recording_real_client(
    resolved_tenant_ctx: TenantContext,
) -> tuple[ToolClient, list[httpx.Request]]:
    """Real ToolClient backed by a recording MockTransport; grant gate is live."""
    captured: list[httpx.Request] = []

    def _handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        import json as _json

        return httpx.Response(
            status_code=202,
            headers={"content-type": "application/json"},
            content=_json.dumps({"status": "accepted"}).encode(),
        )

    transport = httpx.MockTransport(_handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=resolved_tenant_ctx)
    set_tool_client(client)
    return client, captured


# ---------------------------------------------------------------------------
# Registry tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_manage_booking_tools_count() -> None:
    """MANAGE_BOOKING_TOOLS has exactly 6 entries."""
    assert len(MANAGE_BOOKING_TOOLS) == 6


@pytest.mark.unit
def test_manage_booking_tools_names() -> None:
    """Each tool's .name equals the spec name exactly."""
    actual_names = {t.name for t in MANAGE_BOOKING_TOOLS}
    assert actual_names == EXPECTED_TOOL_NAMES


# ---------------------------------------------------------------------------
# No forbidden imports / single-call heuristic
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_no_forbidden_imports_in_manage_booking() -> None:
    """manage_booking.py must not import httpx, sqlalchemy, psycopg, requests, or socket."""
    import cordia_agent.tools.manage_booking as mb_module

    source = inspect.getsource(mb_module)
    import_lines = [
        line.strip()
        for line in source.splitlines()
        if line.strip().startswith("import ") or line.strip().startswith("from ")
    ]
    imports_text = "\n".join(import_lines)
    for forbidden in ("httpx", "sqlalchemy", "psycopg", "requests", "socket"):
        assert forbidden not in imports_text, (
            f"manage_booking.py must not import '{forbidden}'; found in: {imports_text!r}"
        )


@pytest.mark.unit
def test_each_tool_body_has_single_call_tool_reference() -> None:
    """Each manage-booking tool function body contains exactly one 'call_tool' reference.

    Inspect the underlying coroutine (the @tool decorator wraps the raw function);
    assert all 6 tools were inspected so the check can never silently pass vacuously.
    """
    inspected: list[str] = []
    for t in MANAGE_BOOKING_TOOLS:
        coro = t.coroutine
        assert coro is not None, f"Tool '{t.name}' must be an async (coroutine) tool"
        src = inspect.getsource(coro)
        count = src.count("call_tool")
        inspected.append(t.name)
        assert count == 1, (
            f"Tool '{t.name}' expected exactly 1 call_tool reference, got {count}"
        )
    assert len(inspected) == 6, (
        f"Did not inspect all 6 tools; inspected only {sorted(inspected)}"
    )
    assert sorted(inspected) == sorted(EXPECTED_TOOL_NAMES), (
        f"Tool names mismatch; inspected {sorted(inspected)}"
    )


# ---------------------------------------------------------------------------
# Delegation tests — each tool calls fake.call_tool exactly once
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_find_manageable_bookings_delegates(fake_client: FakeClient) -> None:
    await find_manageable_bookings.ainvoke({"phone_e164": "+61400000000"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "find_manageable_bookings"
    assert call["method"] == "GET"
    assert call["params"] is not None
    assert call["params"]["phone_e164"] == "+61400000000"


@pytest.mark.unit
async def test_get_booking_details_delegates(fake_client: FakeClient) -> None:
    await get_booking_details.ainvoke({"booking_id": "bk_001"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "get_booking_details"
    assert call["method"] == "GET"
    assert "bk_001" in call["path"]


@pytest.mark.unit
async def test_get_booking_details_with_grant_delegates(
    fake_client: FakeClient,
) -> None:
    await get_booking_details.ainvoke({"booking_id": "bk_001", "grant_id": "grant_abc"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["grant_id"] == "grant_abc"


@pytest.mark.unit
async def test_validate_booking_change_delegates(fake_client: FakeClient) -> None:
    await validate_booking_change.ainvoke(
        {
            "target_booking_id": "bk_001",
            "expected_booking_version": 3,
            "requested_fields": {"party_size": 4},
            "grant_id": "grant_abc",
        }
    )
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "validate_booking_change"
    assert call["method"] == "POST"
    assert "bk_001" in call["path"]
    assert call["json"] is not None
    assert call["json"]["grant_id"] == "grant_abc"


@pytest.mark.unit
async def test_submit_booking_modification_delegates(fake_client: FakeClient) -> None:
    inp = SubmitModificationInput(
        target_booking_id="bk_001",
        expected_booking_version=2,
        operation_id="op_mod_001",
        modification_idempotency_key="idem_mod_001",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        confirmation=_minimal_confirmation_dict(),
        grant_id="grant_mod_001",
    )
    await submit_booking_modification.ainvoke(inp.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "submit_booking_modification"
    assert call["method"] == "POST"
    assert call["operation_id"] == "op_mod_001"
    assert call["idempotency_key"] == "idem_mod_001"
    assert call["grant_id"] == "grant_mod_001"


@pytest.mark.unit
async def test_submit_booking_cancellation_delegates(fake_client: FakeClient) -> None:
    inp = SubmitCancellationInput(
        target_booking_id="bk_001",
        expected_booking_version=2,
        operation_id="op_can_001",
        cancellation_idempotency_key="idem_can_001",
        reason_code="customer_request",
        grant_id="grant_can_001",
    )
    await submit_booking_cancellation.ainvoke(inp.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "submit_booking_cancellation"
    assert call["method"] == "POST"
    assert call["operation_id"] == "op_can_001"
    assert call["idempotency_key"] == "idem_can_001"
    assert call["grant_id"] == "grant_can_001"


@pytest.mark.unit
async def test_get_booking_operation_status_delegates(fake_client: FakeClient) -> None:
    await get_booking_operation_status.ainvoke({"operation_id": "op_bk_001"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "get_booking_operation_status"
    assert call["method"] == "GET"
    assert "op_bk_001" in call["path"]


# ---------------------------------------------------------------------------
# Inv-11 grant gate — REAL ToolClient, recording MockTransport
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_submit_booking_modification_no_grant_returns_verification_required(
    recording_real_client: tuple[ToolClient, list[httpx.Request]],
) -> None:
    """submit_booking_modification with no grant_id → verification_required, 0 transport hits (Inv-11)."""
    _client, captured = recording_real_client

    # Build minimal input but override grant_id to empty string — the schema
    # requires a str, so we pass "" which is falsy.
    inp = SubmitModificationInput(
        target_booking_id="bk_001",
        expected_booking_version=2,
        operation_id="op_mod_gate",
        modification_idempotency_key="idem_mod_gate",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        confirmation=_minimal_confirmation_dict(),
        grant_id="PLACEHOLDER",  # will be overridden below
    )
    payload = inp.model_dump(mode="json")
    # Force grant_id to empty so the gate fires.
    payload["grant_id"] = ""

    result = await submit_booking_modification.ainvoke(payload)

    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is False
    assert result.status == "verification_required", (
        f"Expected verification_required, got {result.status!r}"
    )
    assert len(captured) == 0, (
        f"Inv-11: transport must NOT be hit when grant_id is absent; got {len(captured)} hits"
    )


@pytest.mark.unit
async def test_submit_booking_cancellation_no_grant_returns_verification_required(
    recording_real_client: tuple[ToolClient, list[httpx.Request]],
) -> None:
    """submit_booking_cancellation with no grant_id → verification_required, 0 transport hits (Inv-11)."""
    _client, captured = recording_real_client

    inp = SubmitCancellationInput(
        target_booking_id="bk_001",
        expected_booking_version=2,
        operation_id="op_can_gate",
        cancellation_idempotency_key="idem_can_gate",
        reason_code="customer_request",
        grant_id="PLACEHOLDER",
    )
    payload = inp.model_dump(mode="json")
    payload["grant_id"] = ""

    result = await submit_booking_cancellation.ainvoke(payload)

    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is False
    assert result.status == "verification_required", (
        f"Expected verification_required, got {result.status!r}"
    )
    assert len(captured) == 0, (
        f"Inv-11: transport must NOT be hit when grant_id is absent; got {len(captured)} hits"
    )


@pytest.mark.unit
async def test_submit_booking_modification_with_grant_hits_transport(
    recording_real_client: tuple[ToolClient, list[httpx.Request]],
) -> None:
    """submit_booking_modification with a valid grant_id hits the transport and sets X-Grant-Id."""
    _client, captured = recording_real_client

    inp = SubmitModificationInput(
        target_booking_id="bk_002",
        expected_booking_version=2,
        operation_id="op_mod_with_grant",
        modification_idempotency_key="idem_mod_with_grant",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        confirmation=_minimal_confirmation_dict(),
        grant_id="grant_valid_001",
    )
    result = await submit_booking_modification.ainvoke(inp.model_dump(mode="json"))

    assert isinstance(result, ToolResultEnvelope)
    assert len(captured) == 1, (
        f"Expected 1 transport hit with valid grant_id, got {len(captured)}"
    )
    assert captured[0].headers.get("X-Grant-Id") == "grant_valid_001", (
        f"X-Grant-Id header not set; headers: {dict(captured[0].headers)}"
    )
    reset_tool_client()


@pytest.mark.unit
async def test_submit_booking_cancellation_with_grant_hits_transport(
    recording_real_client: tuple[ToolClient, list[httpx.Request]],
) -> None:
    """submit_booking_cancellation with a valid grant_id hits the transport and sets X-Grant-Id."""
    _client, captured = recording_real_client

    inp = SubmitCancellationInput(
        target_booking_id="bk_002",
        expected_booking_version=2,
        operation_id="op_can_with_grant",
        cancellation_idempotency_key="idem_can_with_grant",
        reason_code="customer_request",
        grant_id="grant_valid_002",
    )
    result = await submit_booking_cancellation.ainvoke(inp.model_dump(mode="json"))

    assert isinstance(result, ToolResultEnvelope)
    assert len(captured) == 1, (
        f"Expected 1 transport hit with valid grant_id, got {len(captured)}"
    )
    assert captured[0].headers.get("X-Grant-Id") == "grant_valid_002", (
        f"X-Grant-Id header not set; headers: {dict(captured[0].headers)}"
    )
    reset_tool_client()
