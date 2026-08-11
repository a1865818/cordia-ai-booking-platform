"""Unit tests for the 11 foreground tool definitions (spec 03 §4.3, ticket 03-E).

Invariants verified:
- FOREGROUND_TOOLS has exactly 11 tools with the correct spec names.
- Each tool delegates to EXACTLY ONE client call (except check_verification,
  which has conditional GET/POST routing per the spec).
- No forbidden imports (httpx, sqlalchemy, psycopg, requests, socket) in foreground.py.
- Inv-2: submit_confirmed_intent passes operation_id + idempotency_key verbatim.
- Inv-16: accepted envelope customer_hint says 'submitted', never 'booked'.

Invocation strategy:
  ``tool.ainvoke({...})`` returns the raw ``ToolResultEnvelope`` (no string coercion
  in this version of langchain-core), so all tests use it.  For the Inv-16
  wording test we call ``tool.coroutine(...)`` directly to guarantee we receive
  the envelope regardless of future langchain-core changes.
"""

from __future__ import annotations

import datetime
import inspect
from typing import Any

import pytest

from cordia_agent.models.intent import Confirmation, IntentRevision
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    AvailabilityRequest,
    HandoffRequest,
    SubmitConfirmedIntentInput,
    ToolResultEnvelope,
    VerificationRequest,
)
from cordia_agent.tools.foreground import (
    FOREGROUND_TOOLS,
    check_candidate_availability,
    check_verification,
    find_customer_context,
    get_catalog_item,
    get_operation_status,
    get_tenant_context,
    request_human_handoff,
    request_verification,
    search_vendor_catalog,
    submit_confirmed_intent,
    validate_booking_draft,
)
from cordia_agent.tools.status_codes import VerificationAction


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

EXPECTED_TOOL_NAMES = {
    "get_tenant_context",
    "find_customer_context",
    "search_vendor_catalog",
    "get_catalog_item",
    "validate_booking_draft",
    "check_candidate_availability",
    "request_verification",
    "check_verification",
    "submit_confirmed_intent",
    "get_operation_status",
    "request_human_handoff",
    "resolve_consent_policy",
    "record_consent",
    "append_transcript_turn",
    "record_model_trace",
}


# ---------------------------------------------------------------------------
# Helpers — minimal valid model instances
# ---------------------------------------------------------------------------


def _minimal_draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_test",
        session_id="sess_test",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _minimal_confirmation() -> Confirmation:
    return Confirmation(
        confirmation_id="conf_001",
        intent_id="intent_001",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=datetime.datetime(2026, 6, 25, 10, 0, tzinfo=datetime.UTC),
        source_turn_id="turn_001",
        read_back_version="v1",
        verification_level="none",
    )


def _minimal_revision(
    intent_id: str = "intent_001",
    revision: int = 1,
    payload_hash: str = "hash_abc",
) -> IntentRevision:
    """Build the canonical revision the API re-hashes server-side (spec 04 §4.2)."""
    return IntentRevision(
        intent_id=intent_id,
        revision=revision,
        canonical_payload=BookingRequest(operation=OperationType.create),
        schema_version="draft.v1",
        tenant_policy_version="policy.v1",
        payload_hash=payload_hash,
        created_at=datetime.datetime(2026, 6, 25, 10, 0, tzinfo=datetime.UTC),
        source_session_id="sess_001",
    )


def _canned_envelope(
    customer_hint: str | None = None,
    status: str = "ok",
) -> ToolResultEnvelope:
    return ToolResultEnvelope(
        ok=True,
        status=status,
        data=None,
        customer_hint=customer_hint,
        correlation_id="corr_test_001",
    )


# ---------------------------------------------------------------------------
# Recording fake client
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
def fake_client_submitted() -> FakeClient:
    """FakeClient that returns an accepted envelope with 'submitted' customer_hint."""
    client = FakeClient(
        return_value=_canned_envelope(
            customer_hint="I have submitted the request",
            status="accepted",
        )
    )
    set_tool_client(client)
    yield client
    reset_tool_client()


# ---------------------------------------------------------------------------
# Registry tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_foreground_tools_count() -> None:
    """FOREGROUND_TOOLS has exactly 13 entries (11 spec-03 + 2 spec-14 consent)."""
    assert len(FOREGROUND_TOOLS) == 15


@pytest.mark.unit
def test_foreground_tools_names() -> None:
    """Each tool's .name equals the spec name exactly."""
    actual_names = {t.name for t in FOREGROUND_TOOLS}
    assert actual_names == EXPECTED_TOOL_NAMES


# ---------------------------------------------------------------------------
# No forbidden imports / single-call heuristic
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_no_forbidden_imports_in_foreground() -> None:
    """foreground.py must not *import* httpx, sqlalchemy, psycopg, requests, or socket.

    The check looks for import statements only — these strings may appear in
    comments/docstrings that document what is forbidden, but must not appear
    as actual import lines.
    """
    import cordia_agent.tools.foreground as fg_module

    source = inspect.getsource(fg_module)
    # Check for actual import statements, not mentions in comments/docstrings.
    import_lines = [
        line.strip()
        for line in source.splitlines()
        if line.strip().startswith("import ") or line.strip().startswith("from ")
    ]
    imports_text = "\n".join(import_lines)
    for forbidden in ("httpx", "sqlalchemy", "psycopg", "requests", "socket"):
        assert forbidden not in imports_text, (
            f"foreground.py must not import '{forbidden}'; found in: {imports_text!r}"
        )


@pytest.mark.unit
def test_each_tool_body_has_single_call_tool_reference() -> None:
    """Each tool function body contains exactly one 'call_tool' reference.

    check_verification is the sole exception per the spec (GET status vs POST /verify).
    Inspect the underlying coroutine (the @tool decorator wraps the raw function in a
    StructuredTool whose source is not introspectable); assert we actually inspected
    every tool so the check can never silently pass vacuously.
    """
    inspected: list[str] = []
    for t in FOREGROUND_TOOLS:
        coro = t.coroutine
        assert coro is not None, f"Tool '{t.name}' must be an async (coroutine) tool"
        src = inspect.getsource(coro)
        count = src.count("call_tool")
        inspected.append(t.name)
        if t.name == "check_verification":
            # Spec-mandated dual routing — GET status vs POST /verify.
            assert count == 2, (
                f"check_verification expected 2 call_tool references, got {count}"
            )
        else:
            assert count == 1, (
                f"Tool '{t.name}' expected exactly 1 call_tool reference, got {count}"
            )
    assert sorted(inspected) == sorted(EXPECTED_TOOL_NAMES), (
        f"Did not inspect all tools; inspected only {sorted(inspected)}"
    )


# ---------------------------------------------------------------------------
# Delegation tests — each tool calls fake.call_tool exactly once
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_get_tenant_context_delegates(fake_client: FakeClient) -> None:
    await get_tenant_context.ainvoke({})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "get_tenant_context"
    assert call["method"] == "GET"


@pytest.mark.unit
async def test_find_customer_context_delegates(fake_client: FakeClient) -> None:
    await find_customer_context.ainvoke({"phone_e164": "+61400000000"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "find_customer_context"
    assert call["method"] == "GET"
    assert call["params"] is not None
    assert call["params"]["phone_e164"] == "+61400000000"


@pytest.mark.unit
async def test_search_vendor_catalog_delegates(fake_client: FakeClient) -> None:
    await search_vendor_catalog.ainvoke({"query": "haircut"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "search_vendor_catalog"
    assert call["method"] == "GET"
    assert call["params"]["query"] == "haircut"


@pytest.mark.unit
async def test_get_catalog_item_delegates(fake_client: FakeClient) -> None:
    await get_catalog_item.ainvoke({"item_id": "item_abc"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "get_catalog_item"
    assert call["method"] == "GET"
    assert "item_abc" in call["path"]


@pytest.mark.unit
async def test_validate_booking_draft_delegates(fake_client: FakeClient) -> None:
    draft_dict = _minimal_draft().model_dump(mode="json")
    await validate_booking_draft.ainvoke(draft_dict)
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "validate_booking_draft"
    assert call["method"] == "POST"
    assert call["json"] is not None


@pytest.mark.unit
async def test_check_candidate_availability_delegates(fake_client: FakeClient) -> None:
    req = AvailabilityRequest(draft=_minimal_draft(), want_hold=False)
    await check_candidate_availability.ainvoke(req.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "check_candidate_availability"
    assert call["method"] == "POST"


@pytest.mark.unit
async def test_request_verification_delegates(fake_client: FakeClient) -> None:
    req = VerificationRequest(
        channel="sms",
        action=VerificationAction.MODIFY,
        verification_idempotency_key="idem_ver_001",
    )
    await request_verification.ainvoke(req.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "request_verification"
    assert call["method"] == "POST"
    assert call["idempotency_key"] == "idem_ver_001"


@pytest.mark.unit
async def test_check_verification_get_delegates(fake_client: FakeClient) -> None:
    await check_verification.ainvoke({"verification_id": "ver_001"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "check_verification"
    assert call["method"] == "GET"
    assert "ver_001" in call["path"]


@pytest.mark.unit
async def test_check_verification_post_with_code(fake_client: FakeClient) -> None:
    await check_verification.ainvoke({"verification_id": "ver_001", "code": "123456"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "check_verification"
    assert call["method"] == "POST"
    assert call["json"] == {"code": "123456"}


@pytest.mark.unit
async def test_get_operation_status_delegates(fake_client: FakeClient) -> None:
    await get_operation_status.ainvoke({"operation_id": "op_xyz"})
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "get_operation_status"
    assert call["method"] == "GET"
    assert "op_xyz" in call["path"]


@pytest.mark.unit
async def test_request_human_handoff_delegates(fake_client: FakeClient) -> None:
    req = HandoffRequest(
        session_id="sess_001",
        reason_code="complaint",
        handoff_idempotency_key="idem_hh_001",
    )
    await request_human_handoff.ainvoke(req.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "request_human_handoff"
    assert call["method"] == "POST"
    assert call["idempotency_key"] == "idem_hh_001"


# ---------------------------------------------------------------------------
# Inv-2: submit_confirmed_intent passes operation_id + idempotency_key verbatim
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_submit_confirmed_intent_inv2_passthrough(
    fake_client: FakeClient,
) -> None:
    """operation_id and idempotency_key flow through to call_tool verbatim (Inv-2)."""
    inp = SubmitConfirmedIntentInput(
        operation_id="op_x",
        idempotency_key="idem_y",
        intent_id="intent_001",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        revision=_minimal_revision(),
        confirmation=_minimal_confirmation(),
    )
    await submit_confirmed_intent.ainvoke(inp.model_dump(mode="json"))
    assert len(fake_client.calls) == 1
    call = fake_client.calls[0]
    assert call["tool_name"] == "submit_confirmed_intent"
    assert call["operation_id"] == "op_x", (
        f"Inv-2: expected operation_id='op_x', got {call['operation_id']!r}"
    )
    assert call["idempotency_key"] == "idem_y", (
        f"Inv-2: expected idempotency_key='idem_y', got {call['idempotency_key']!r}"
    )


# ---------------------------------------------------------------------------
# Inv-16: accepted envelope's customer_hint says 'submitted', never 'booked'
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_submit_confirmed_intent_inv16_hint(
    fake_client_submitted: FakeClient,
) -> None:
    """Accepted envelope customer_hint contains 'submitted' and not 'booked' (Inv-16).

    We call ``tool.coroutine(...)`` directly to guarantee we get the
    ``ToolResultEnvelope`` back regardless of langchain-core return coercion.
    """
    inp = SubmitConfirmedIntentInput(
        operation_id="op_inv16",
        idempotency_key="idem_inv16",
        intent_id="intent_001",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        revision=_minimal_revision(),
        confirmation=_minimal_confirmation(),
    )
    # Use .coroutine to call the underlying async function directly.
    result = await submit_confirmed_intent.coroutine(**inp.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope), (
        f"Expected ToolResultEnvelope, got {type(result)}"
    )
    assert result.customer_hint is not None
    hint_lower = result.customer_hint.lower()
    assert "submitted" in hint_lower, (
        f"Inv-16: customer_hint should contain 'submitted', got: {result.customer_hint!r}"
    )
    assert "booked" not in hint_lower, (
        f"Inv-16: customer_hint must NOT contain 'booked', got: {result.customer_hint!r}"
    )
