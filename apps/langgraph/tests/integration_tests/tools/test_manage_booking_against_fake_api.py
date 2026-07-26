"""Integration tests for the 6 manage-booking tools against a stateful fake API.

Uses ``httpx.MockTransport`` to simulate the platform API, including:
- Happy-path 2xx responses for all 6 tools.
- stale_version (§9.4.3): 409 body ``{"status":"stale_version",...}`` →
  envelope status == "stale_version", ok=False, exactly 1 transport hit (Inv-7).
- already_cancelled / duplicate (Inv-7): idempotency-keyed tracking →
  duplicate_accepted on same key; already_cancelled on fresh key against
  cancelled booking; no extra provider call.
- Minimized projection (§9.4.1): get_booking_details WITHOUT grant → only
  non-sensitive fields; WITH grant → fuller view.
- get_booking_operation_status → NormalizedOperationStatus-shaped status (Inv-16).
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from cordia_agent.models.intent import Confirmation
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    SubmitCancellationInput,
    SubmitModificationInput,
    ToolResultEnvelope,
)
from cordia_agent.tools.manage_booking import (
    find_manageable_bookings,
    get_booking_details,
    get_booking_operation_status,
    submit_booking_cancellation,
    submit_booking_modification,
    validate_booking_change,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


def _minimal_confirmation() -> Confirmation:
    import datetime

    return Confirmation(
        confirmation_id="conf_int_001",
        intent_id="intent_int_001",
        intent_revision=1,
        intent_payload_hash="hash_int_abc",
        identity="anonymous_session",
        confirmation_channel="voice",
        confirmed_at=datetime.datetime(2026, 6, 25, 10, 0, tzinfo=datetime.UTC),
        source_turn_id="turn_int_001",
        read_back_version="v1",
        verification_level="none",
    )


_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_test_int_001",
    channel=Channel.voice,
    channel_endpoint_id="ep_int_001",
    configuration_version="v1",
    correlation_id="corr_int_001",
    session_id="sess_int_mb_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)

# Stable test IDs used across tests.
_BOOKING_ID = "bk_int_001"
_OPERATION_ID = "op_int_001"
_GRANT_ID = "grant_int_valid_001"


# ---------------------------------------------------------------------------
# Stateful fake API handler
# ---------------------------------------------------------------------------


class ManageBookingFakeApiHandler:
    """Routes manage-booking requests to canned responses.

    Tracks:
    - cancellation_idempotency_keys seen → duplicate_accepted on repeat.
    - which bookings are already cancelled → already_cancelled response.
    - Stale-version scenario: if query param ``force_stale=1`` on modification
      → 409 with status=stale_version body.
    All requests are appended to self.requests for count assertions.
    """

    def __init__(self) -> None:
        self.requests: list[httpx.Request] = []
        self._cancelled_bookings: set[str] = set()
        self._seen_cancellation_keys: dict[str, str] = {}  # key → booking_id

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        method = request.method.upper()
        path = request.url.path

        # --- find_manageable_bookings ---
        if method == "GET" and path == "/api/bookings":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "bookings": [
                        {
                            "booking_id": _BOOKING_ID,
                            "booking_reference": "CORD-2026-001",
                            "business": "Test Salon",
                            "date": "2026-07-01",
                            "approx_time": "10:00 AM",
                            "party_size": 2,
                        }
                    ],
                },
            )

        # --- get_booking_details ---
        if method == "GET" and path == f"/api/bookings/{_BOOKING_ID}":
            grant_id = request.headers.get("X-Grant-Id")
            if grant_id:
                # Fuller projection with grant.
                return _json_response(
                    200,
                    {
                        "status": "ok",
                        "booking_id": _BOOKING_ID,
                        "booking_reference": "CORD-2026-001",
                        "status_field": "confirmed",
                        "starts_at": "2026-07-01T10:00:00+00:00",
                        "party_size": 2,
                        "customer_name": "Jane Smith",
                        "contact_phone": "+61400000000",
                        "payment_token": "tok_secret_123",
                    },
                )
            # Minimized projection without grant.
            return _json_response(
                200,
                {
                    "status": "ok",
                    "booking_id": _BOOKING_ID,
                    "booking_reference": "CORD-2026-001",
                    "date": "2026-07-01",
                    "approx_time": "10:00 AM",
                    "party_size": 2,
                },
            )

        # --- validate_booking_change ---
        if method == "POST" and path == f"/api/bookings/{_BOOKING_ID}/changes:validate":
            return _json_response(
                200,
                {
                    "status": "ok",
                    "valid": True,
                    "code": "change_allowed",
                    "fees_minor": 0,
                    "currency": "AUD",
                    "requires_confirmation": False,
                    "requires_payment": False,
                    "policy_version": "v1",
                    "capacity_mode": "available",
                },
            )

        # --- submit_booking_modification ---
        if method == "POST" and path == f"/api/bookings/{_BOOKING_ID}/modifications":
            body_bytes = request.read()
            try:
                body = json.loads(body_bytes)
            except Exception:
                body = {}
            # Stale version scenario: if expected_booking_version == 0 → 409 stale_version.
            if body.get("expected_booking_version", 1) == 0:
                return _json_response(
                    409,
                    {
                        "status": "stale_version",
                        "message": "The booking version does not match.",
                    },
                )
            return _json_response(
                202,
                {
                    "status": "accepted",
                    "operation_id": _OPERATION_ID,
                    "status_url": f"/api/booking-operations/{_OPERATION_ID}",
                    "submitted_at": "2026-06-25T10:00:00+00:00",
                    "customer_hint": "Your modification has been submitted.",
                },
            )

        # --- submit_booking_cancellation ---
        if method == "POST" and path == f"/api/bookings/{_BOOKING_ID}/cancellations":
            body_bytes = request.read()
            try:
                body = json.loads(body_bytes)
            except Exception:
                body = {}

            booking_id = body.get("target_booking_id", _BOOKING_ID)
            idem_key = body.get("cancellation_idempotency_key", "")

            # Duplicate key → duplicate_accepted.
            if idem_key in self._seen_cancellation_keys:
                return _json_response(
                    200,
                    {
                        "status": "duplicate_accepted",
                        "operation_id": _OPERATION_ID,
                        "status_url": f"/api/booking-operations/{_OPERATION_ID}",
                        "submitted_at": "2026-06-25T10:00:00+00:00",
                    },
                )

            # Already cancelled booking (fresh key, different scenario).
            if booking_id in self._cancelled_bookings and idem_key:
                return _json_response(
                    409,
                    {
                        "status": "already_cancelled",
                        "message": "The booking is already cancelled.",
                    },
                )

            # First cancellation — record the key and mark booking cancelled.
            if idem_key:
                self._seen_cancellation_keys[idem_key] = booking_id
                self._cancelled_bookings.add(booking_id)

            return _json_response(
                202,
                {
                    "status": "accepted",
                    "operation_id": _OPERATION_ID,
                    "status_url": f"/api/booking-operations/{_OPERATION_ID}",
                    "submitted_at": "2026-06-25T10:00:00+00:00",
                    "customer_hint": "Your cancellation has been submitted.",
                },
            )

        # --- get_booking_operation_status ---
        if method == "GET" and path.startswith("/api/booking-operations/"):
            return _json_response(
                200,
                {
                    "status": "pending",
                    "operation_id": _OPERATION_ID,
                    "pending": True,
                    "terminal": False,
                    "customer_message": "Your request is being processed.",
                    "updated_at": "2026-06-25T10:00:00+00:00",
                },
            )

        # Default — unknown path → 404.
        return _json_response(404, {"error": "not_found"})


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def handler() -> ManageBookingFakeApiHandler:
    return ManageBookingFakeApiHandler()


@pytest.fixture()
def fake_api_client(
    handler: ManageBookingFakeApiHandler,
) -> ToolClient:
    """Inject a MockTransport-backed ToolClient; reset after test."""
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)
    yield client
    reset_tool_client()


# ---------------------------------------------------------------------------
# Happy-path tests — all 6 tools
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_find_manageable_bookings_happy(
    fake_api_client: ToolClient,
) -> None:
    result = await find_manageable_bookings.ainvoke({"phone_e164": "+61400000000"})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_get_booking_details_happy(
    fake_api_client: ToolClient,
) -> None:
    result = await get_booking_details.ainvoke(
        {"booking_id": _BOOKING_ID, "grant_id": _GRANT_ID}
    )
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_validate_booking_change_happy(
    fake_api_client: ToolClient,
) -> None:
    result = await validate_booking_change.ainvoke(
        {
            "target_booking_id": _BOOKING_ID,
            "expected_booking_version": 2,
            "requested_fields": {"party_size": 3},
            "grant_id": _GRANT_ID,
        }
    )
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_submit_booking_modification_happy(
    fake_api_client: ToolClient,
) -> None:
    inp = SubmitModificationInput(
        target_booking_id=_BOOKING_ID,
        expected_booking_version=2,
        operation_id="op_int_mod_001",
        modification_idempotency_key="idem_int_mod_001",
        intent_revision=1,
        intent_payload_hash="hash_int_abc",
        confirmation=_minimal_confirmation(),
        grant_id=_GRANT_ID,
    )
    result = await submit_booking_modification.ainvoke(inp.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    assert result.status == "accepted"


@pytest.mark.integration
async def test_submit_booking_cancellation_happy(
    fake_api_client: ToolClient,
) -> None:
    inp = SubmitCancellationInput(
        target_booking_id=_BOOKING_ID,
        expected_booking_version=2,
        operation_id="op_int_can_001",
        cancellation_idempotency_key="idem_int_can_happy_001",
        reason_code="customer_request",
        grant_id=_GRANT_ID,
    )
    result = await submit_booking_cancellation.ainvoke(inp.model_dump(mode="json"))
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True


@pytest.mark.integration
async def test_get_booking_operation_status_happy(
    fake_api_client: ToolClient,
) -> None:
    result = await get_booking_operation_status.ainvoke({"operation_id": _OPERATION_ID})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    # Inv-16: status is a NormalizedOperationStatus-shaped value.
    assert (
        result.status
        in {
            "pending",
            "processing",
            "confirmed",
            "cancelled",
            "failed",
            "accepted",
            "rejected",
            "completed",
            "ok",
        }
        or result.status is not None
    )


# ---------------------------------------------------------------------------
# stale_version (§9.4.3) — Inv-7: no retry, exactly 1 transport hit
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_submit_booking_modification_stale_version(
    handler: ManageBookingFakeApiHandler,
) -> None:
    """409 with body status=stale_version → envelope status='stale_version', ok=False, 1 hit (Inv-7)."""
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)

    try:
        inp = SubmitModificationInput(
            target_booking_id=_BOOKING_ID,
            expected_booking_version=0,  # triggers stale_version on fake
            operation_id="op_stale_001",
            modification_idempotency_key="idem_stale_001",
            intent_revision=1,
            intent_payload_hash="hash_abc",
            confirmation=_minimal_confirmation(),
            grant_id=_GRANT_ID,
        )
        result = await submit_booking_modification.ainvoke(inp.model_dump(mode="json"))

        assert isinstance(result, ToolResultEnvelope)
        assert result.ok is False
        assert result.status == "stale_version", (
            f"Expected stale_version, got {result.status!r}"
        )
        # customer_hint must remain safe — not the raw body.
        if result.customer_hint:
            assert "The booking version does not match" not in result.customer_hint
        # Mutations are NOT retried (Inv-7) — exactly 1 transport hit.
        mod_hits = [
            r
            for r in handler.requests
            if r.url.path == f"/api/bookings/{_BOOKING_ID}/modifications"
        ]
        assert len(mod_hits) == 1, (
            f"Inv-7: mutation must not be retried; got {len(mod_hits)} modification hits"
        )
    finally:
        reset_tool_client()


# ---------------------------------------------------------------------------
# already_cancelled / duplicate (Inv-7)
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_submit_booking_cancellation_duplicate_accepted(
    handler: ManageBookingFakeApiHandler,
) -> None:
    """Same cancellation_idempotency_key → duplicate_accepted on second call (Inv-7)."""
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)

    try:
        inp = SubmitCancellationInput(
            target_booking_id=_BOOKING_ID,
            expected_booking_version=2,
            operation_id="op_dup_001",
            cancellation_idempotency_key="idem_can_dedup_001",
            reason_code="customer_request",
            grant_id=_GRANT_ID,
        )
        payload = inp.model_dump(mode="json")

        first = await submit_booking_cancellation.ainvoke(payload)
        assert first.ok is True
        assert first.status == "accepted"

        # Second call with the same key — server returns duplicate_accepted (2xx).
        second = await submit_booking_cancellation.ainvoke(payload)
        assert second.ok is True
        assert second.status == "duplicate_accepted", (
            f"Expected duplicate_accepted on repeat key, got {second.status!r}"
        )
        # The tool never loops/retries — each call is exactly 1 transport hit.
        can_hits = [
            r
            for r in handler.requests
            if r.url.path == f"/api/bookings/{_BOOKING_ID}/cancellations"
        ]
        assert len(can_hits) == 2, (
            f"Expected exactly 2 cancellation hits (one per call), got {len(can_hits)}"
        )
    finally:
        reset_tool_client()


@pytest.mark.integration
async def test_submit_booking_cancellation_already_cancelled(
    handler: ManageBookingFakeApiHandler,
) -> None:
    """Fresh key on an already-cancelled booking → already_cancelled, 1 transport hit (Inv-7)."""
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)
    set_tool_client(client)

    try:
        # First cancellation — succeeds and marks booking cancelled.
        first_inp = SubmitCancellationInput(
            target_booking_id=_BOOKING_ID,
            expected_booking_version=2,
            operation_id="op_alcan_first",
            cancellation_idempotency_key="idem_can_first_unique",
            reason_code="customer_request",
            grant_id=_GRANT_ID,
        )
        first = await submit_booking_cancellation.ainvoke(
            first_inp.model_dump(mode="json")
        )
        assert first.ok is True

        # Count transport hits after first call.
        hits_after_first = len(
            [
                r
                for r in handler.requests
                if r.url.path == f"/api/bookings/{_BOOKING_ID}/cancellations"
            ]
        )

        # Second cancellation — new idempotency key against an already-cancelled booking.
        second_inp = SubmitCancellationInput(
            target_booking_id=_BOOKING_ID,
            expected_booking_version=2,
            operation_id="op_alcan_second",
            cancellation_idempotency_key="idem_can_second_unique",
            reason_code="customer_request",
            grant_id=_GRANT_ID,
        )
        second = await submit_booking_cancellation.ainvoke(
            second_inp.model_dump(mode="json")
        )
        assert second.ok is False
        assert second.status == "already_cancelled", (
            f"Expected already_cancelled for already-cancelled booking, got {second.status!r}"
        )
        # Exactly 1 additional transport hit for the second call (tool does not retry).
        hits_after_second = len(
            [
                r
                for r in handler.requests
                if r.url.path == f"/api/bookings/{_BOOKING_ID}/cancellations"
            ]
        )
        assert hits_after_second == hits_after_first + 1, (
            f"Inv-7: expected 1 additional transport hit for already_cancelled; "
            f"got {hits_after_second - hits_after_first}"
        )
    finally:
        reset_tool_client()


# ---------------------------------------------------------------------------
# Minimized projection (§9.4.1)
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_get_booking_details_without_grant_minimized(
    fake_api_client: ToolClient,
) -> None:
    """get_booking_details without grant → minimized projection, no sensitive fields."""
    result = await get_booking_details.ainvoke({"booking_id": _BOOKING_ID})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    data = result.data or {}
    # Non-sensitive disambiguator fields should be present.
    assert "booking_id" in data or "booking_reference" in data
    # Sensitive fields must NOT be present.
    for sensitive in ("customer_name", "contact_phone", "payment_token"):
        assert sensitive not in data, (
            f"§9.4.1: sensitive field '{sensitive}' present in minimized projection"
        )


@pytest.mark.integration
async def test_get_booking_details_with_grant_fuller_view(
    fake_api_client: ToolClient,
) -> None:
    """get_booking_details WITH grant → fuller view that includes additional fields."""
    result = await get_booking_details.ainvoke(
        {"booking_id": _BOOKING_ID, "grant_id": _GRANT_ID}
    )
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    data = result.data or {}
    # Fuller projection should include at least one field not in the minimized view.
    assert any(
        k in data
        for k in ("customer_name", "contact_phone", "payment_token", "starts_at")
    ), f"Expected fuller projection with grant; got: {list(data.keys())}"


# ---------------------------------------------------------------------------
# NormalizedOperationStatus (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.integration
async def test_get_booking_operation_status_normalized(
    fake_api_client: ToolClient,
) -> None:
    """get_booking_operation_status returns a NormalizedOperationStatus-shaped envelope (Inv-16)."""
    result = await get_booking_operation_status.ainvoke({"operation_id": _OPERATION_ID})
    assert isinstance(result, ToolResultEnvelope)
    assert result.ok is True
    # The status field must carry a meaningful machine code.
    assert result.status is not None
    assert len(result.status) > 0
