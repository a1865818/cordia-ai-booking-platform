"""Integration tests for the 11 foreground tools against a stateful fake API.

Uses ``httpx.MockTransport`` to simulate the platform API, including:
- Happy-path 2xx responses for all 11 tools.
- One representative error path (non-2xx → ok=False envelope).
- Dedup (Inv-5/Inv-9): request_verification and request_human_handoff return
  ``already_sent`` / ``existing`` on a repeated call with the same idempotency key.

``set_tool_client`` is used to inject the transport-backed ``ToolClient``.

Invocation strategy:
  ``tool.ainvoke({...})`` returns the raw ``ToolResultEnvelope`` (no string
  coercion in this version of langchain-core).  The underlying coroutine is
  called via ``tool.coroutine(...)`` only where we need to inspect the envelope
  directly and guarantee no wrapping.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.models.intent import Confirmation, IntentRevision
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    AvailabilityRequest,
    HandoffRequest,
    SubmitConfirmedIntentInput,
    ToolResultEnvelope,
    VerificationRequest,
)
from cordia_agent.tools.foreground import (
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
        source_session_id="sess_int_001",
    )


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


# ---------------------------------------------------------------------------
# Stateful fake API handler
# ---------------------------------------------------------------------------

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_test_001",
    channel=Channel.voice,
    channel_endpoint_id="ep_test_001",
    configuration_version="v1",
    correlation_id="corr_int_test_001",
    session_id="sess_int_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


class FakeApiHandler:
    """Routes (method, path-prefix) to canned 2xx responses.

    For request_verification and request_human_handoff it tracks the idempotency
    key (from X-Idempotency-Key header) and returns already_sent / existing on
    a repeated call with the same key (Inv-5/Inv-9).
    """

    def __init__(self) -> None:
        self._sent_verification_keys: set[str] = set()
        self._sent_handoff_keys: set[str] = set()
        # Track all requests for inspection in tests.
        self.requests: list[httpx.Request] = []

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        method = request.method.upper()
        path = request.url.path

        # --- get_tenant_context ---
        if method == "GET" and path == "/api/tenant-context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "tenant_id": "tenant_test_001",
                    "policy_version": "v1",
                    "timezone": "Australia/Sydney",
                    "supported_operations": ["create"],
                    "capabilities": [],
                    "currency": "AUD",
                },
            )

        # --- find_customer_context ---
        if method == "GET" and path == "/api/customers/context":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "customer_id": "cust_001",
                    "display_name": "Jane Smith",
                    "has_tokenized_payment": False,
                },
            )

        # --- search_vendor_catalog ---
        if method == "GET" and path == "/api/catalog/items":
            return _json_response(
                200,
                {"status": "ok", "items": [{"item_id": "item_001", "name": "Haircut"}]},
            )

        # --- get_catalog_item ---
        if method == "GET" and path.startswith("/api/catalog/items/"):
            item_id = path.split("/")[-1]
            return _json_response(
                200,
                {
                    "status": "ok",
                    "item_id": item_id,
                    "name": "Haircut",
                    "price_minor": 4500,
                },
            )

        # --- validate_booking_draft ---
        if method == "POST" and path == "/api/booking-drafts/validate":
            return _json_response(
                200,
                {"status": "ok", "valid": True, "customer_message": "Draft is valid"},
            )

        # --- check_candidate_availability ---
        if method == "POST" and path == "/api/availability/candidates":
            return _json_response(
                200,
                {"status": "ok", "mode": "available", "candidates": []},
            )

        # --- request_verification ---
        if method == "POST" and path == "/api/verifications":
            idem_key = request.headers.get("X-Idempotency-Key", "")
            if idem_key and idem_key in self._sent_verification_keys:
                return _json_response(
                    200,
                    {
                        "status": "already_sent",
                        "verification_id": "ver_existing",
                        "customer_hint": "already_sent",
                    },
                )
            if idem_key:
                self._sent_verification_keys.add(idem_key)
            return _json_response(
                200,
                {
                    "status": "ok",
                    "verification_id": "ver_001",
                    "channel": "sms",
                    "expires_at": "2026-06-25T11:00:00+00:00",
                    "masked_destination": "+61 4** *** 321",
                },
            )

        # --- check_verification (GET status) ---
        if method == "GET" and "/api/verifications/" in path and "/verify" not in path:
            return _json_response(200, {"status": "ok", "grant_id": None})

        # --- check_verification (POST /verify) ---
        if method == "POST" and "/verify" in path:
            return _json_response(200, {"status": "ok", "grant_id": "grant_001"})

        # --- submit_confirmed_intent ---
        if method == "POST" and path == "/api/bookings":
            return _json_response(
                202,
                {
                    "status": "accepted",
                    "operation_id": "op_001",
                    "intent_id": "intent_001",
                    "intent_revision": 1,
                    "status_url": "/api/booking-operations/op_001",
                    "submitted_at": "2026-06-25T10:00:00+00:00",
                    "customer_hint": "I have submitted the request",
                },
            )

        # --- get_operation_status ---
        if method == "GET" and path.startswith("/api/booking-operations/"):
            return _json_response(
                200,
                {"status": "ok", "operation_id": "op_001", "pending": True},
            )

        # --- request_human_handoff ---
        if method == "POST" and path == "/api/handoffs":
            idem_key = request.headers.get("X-Idempotency-Key", "")
            if idem_key and idem_key in self._sent_handoff_keys:
                return _json_response(
                    200,
                    {
                        "status": "existing",
                        "work_item_id": "wi_existing",
                        "queue": "general",
                        "customer_hint": "existing",
                    },
                )
            if idem_key:
                self._sent_handoff_keys.add(idem_key)
            return _json_response(
                200,
                {
                    "status": "ok",
                    "work_item_id": "wi_001",
                    "queue": "general",
                },
            )

        # Default — unknown path → 404
        return _json_response(404, {"error": "not_found"})


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def handler() -> FakeApiHandler:
    return FakeApiHandler()


@pytest.fixture()
def fake_api_client(handler: FakeApiHandler) -> ToolClient:
    """Inject a MockTransport-backed ToolClient; reset after test."""
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)
    yield client
    reset_tool_client()


# ---------------------------------------------------------------------------
# Happy-path tests — all 11 tools
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_get_tenant_context_happy(fake_api_client: ToolClient) -> None:
    result = await get_tenant_context.ainvoke({})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    assert result.status is not None
    assert result.correlation_id is not None


@pytest.mark.integration
async def test_find_customer_context_happy(fake_api_client: ToolClient) -> None:
    result = await find_customer_context.ainvoke({"phone_e164": "+61400000000"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_search_vendor_catalog_happy(fake_api_client: ToolClient) -> None:
    result = await search_vendor_catalog.ainvoke({"query": "haircut"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_get_catalog_item_happy(fake_api_client: ToolClient) -> None:
    result = await get_catalog_item.ainvoke({"item_id": "item_001"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_validate_booking_draft_happy(fake_api_client: ToolClient) -> None:
    result = await validate_booking_draft.ainvoke(
        _minimal_draft().model_dump(mode="json")
    )
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_check_candidate_availability_happy(fake_api_client: ToolClient) -> None:
    req = AvailabilityRequest(draft=_minimal_draft(), want_hold=False)
    result = await check_candidate_availability.ainvoke(req.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_request_verification_happy(fake_api_client: ToolClient) -> None:
    req = VerificationRequest(
        channel="sms",
        action=VerificationAction.MODIFY,
        verification_idempotency_key="idem_ver_int_001",
    )
    result = await request_verification.ainvoke(req.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_check_verification_get_happy(fake_api_client: ToolClient) -> None:
    result = await check_verification.ainvoke({"verification_id": "ver_001"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_check_verification_post_happy(fake_api_client: ToolClient) -> None:
    result = await check_verification.ainvoke(
        {"verification_id": "ver_001", "code": "123456"}
    )
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_submit_confirmed_intent_happy(fake_api_client: ToolClient) -> None:
    inp = SubmitConfirmedIntentInput(
        operation_id="op_int_001",
        idempotency_key="idem_int_001",
        intent_id="intent_001",
        intent_revision=1,
        intent_payload_hash="hash_abc",
        revision=_minimal_revision(),
        confirmation=_minimal_confirmation(),
    )
    result = await submit_confirmed_intent.ainvoke(inp.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    assert result.status is not None


@pytest.mark.integration
async def test_get_operation_status_happy(fake_api_client: ToolClient) -> None:
    result = await get_operation_status.ainvoke({"operation_id": "op_001"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_request_human_handoff_happy(fake_api_client: ToolClient) -> None:
    req = HandoffRequest(
        session_id="sess_001",
        reason_code="complaint",
        handoff_idempotency_key="idem_hh_int_001",
    )
    result = await request_human_handoff.ainvoke(req.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


# ---------------------------------------------------------------------------
# Error-path tests (representative subset)
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_get_catalog_item_not_found() -> None:
    """Non-2xx response maps to ok=False envelope (safe error mapping)."""

    def not_found_handler(request: httpx.Request) -> httpx.Response:
        return _json_response(404, {"error": "not_found"})

    transport = httpx.MockTransport(not_found_handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)
    try:
        result = await get_catalog_item.ainvoke({"item_id": "item_missing"})
        assert isinstance(result, ToolResultEnvelope)
        assert result.ok is False, "Expected ok=False for 404 response"
    finally:
        reset_tool_client()


@pytest.mark.integration
async def test_submit_confirmed_intent_server_error(
    handler: FakeApiHandler,
) -> None:
    """500 from submit_confirmed_intent → ok=False with safe hint (no raw body)."""

    def error_handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/bookings":
            return _json_response(500, {"error": "internal_server_error"})
        return handler(request)

    transport = httpx.MockTransport(error_handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)

    try:
        inp = SubmitConfirmedIntentInput(
            operation_id="op_err_001",
            idempotency_key="idem_err_001",
            intent_id="intent_001",
            intent_revision=1,
            intent_payload_hash="hash_abc",
            revision=_minimal_revision(),
            confirmation=_minimal_confirmation(),
        )
        result = await submit_confirmed_intent.ainvoke(inp.model_dump(mode="json"))
        assert isinstance(result, ToolResultEnvelope)
        assert result.ok is False
        # Raw error text must NOT appear in customer_hint (Inv-14/Inv-16).
        if result.customer_hint:
            assert "internal_server_error" not in result.customer_hint
    finally:
        reset_tool_client()


# ---------------------------------------------------------------------------
# Dedup tests — Inv-5 (request_verification) and Inv-9 (request_human_handoff)
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_request_verification_dedup_returns_already_sent(
    fake_api_client: ToolClient,
) -> None:
    """Second call with the same verification_idempotency_key returns already_sent (Inv-9)."""
    req = VerificationRequest(
        channel="sms",
        action=VerificationAction.MODIFY,
        verification_idempotency_key="idem_dedup_ver_001",
    )
    payload = req.model_dump(mode="json")

    first = await request_verification.ainvoke(payload)
    assert first.ok is True
    assert first.status != "already_sent", (
        f"First call should NOT return already_sent, got: {first.status!r}"
    )

    second = await request_verification.ainvoke(payload)
    assert second.ok is True
    assert second.status == "already_sent", (
        f"Inv-9: second call with same key should return 'already_sent', got: {second.status!r}"
    )


@pytest.mark.integration
async def test_request_human_handoff_dedup_returns_existing(
    fake_api_client: ToolClient,
) -> None:
    """Second call with the same handoff_idempotency_key returns existing (Inv-5)."""
    req = HandoffRequest(
        session_id="sess_dedup_001",
        reason_code="complaint",
        handoff_idempotency_key="idem_dedup_hh_001",
    )
    payload = req.model_dump(mode="json")

    first = await request_human_handoff.ainvoke(payload)
    assert first.ok is True
    assert first.status != "existing", (
        f"First call should NOT return existing, got: {first.status!r}"
    )

    second = await request_human_handoff.ainvoke(payload)
    assert second.ok is True
    assert second.status == "existing", (
        f"Inv-5: second call with same key should return 'existing', got: {second.status!r}"
    )
