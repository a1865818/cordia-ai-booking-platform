"""Unit tests for human_handoff node and helpers (02-K, §8.11/§8.12, Inv-9).

Covers:
  - Well-formed package: human_handoff builds a HandoffPackage with reason +
    draft_snapshot + source_turn_ids; terminal_state == "human_handoff";
    handoff_package dict validates back to HandoffPackage.
  - Idempotency (Inv-9): handoff_idempotency_key is stable for the same
    session+reason (build twice → same key); repeat request against the fake
    returns "existing".
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from cordia_agent.models.handoff import HandoffPackage
from cordia_agent.nodes.human_handoff import (
    _stable_idempotency_key,
    build_handoff_package,
    human_handoff,
)
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

_TENANT_ID = "tenant_hh_test"
_SESSION_ID = "sess_hh_001"

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id=_TENANT_ID,
    channel=Channel.voice,
    channel_endpoint_id="ep_hh_001",
    configuration_version="v1",
    correlation_id="corr_hh_001",
    allowed_policy_ids=["policy_a"],
)


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _handoff_created_body(*, status: str = "created") -> dict:
    """Build a 200 response body for request_human_handoff.

    The ToolClient wraps the entire 200 body as ``ToolResultEnvelope.data``.
    ``parse_handoff_result`` calls ``_payload(env.data, HandoffResult)``
    which strips envelope-only keys before model_validate — so HandoffResult
    fields must be at the TOP level of the body, not nested under ``"data"``.
    """
    return {
        "status": status,
        "work_item_id": "wi_001",
        "queue": "default",
    }


def _minimal_draft() -> BookingDraft:
    import datetime

    return BookingDraft(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.modify,
            target_booking_id="bk_001",
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


@pytest.fixture(autouse=True)
def _reset_after_test():
    yield
    reset_tool_client()


# ---------------------------------------------------------------------------
# _stable_idempotency_key unit tests (pure function)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_stable_idempotency_key_deterministic() -> None:
    """Same session+reason → same key (Inv-9)."""
    key1 = _stable_idempotency_key(_SESSION_ID, "verification_failed")
    key2 = _stable_idempotency_key(_SESSION_ID, "verification_failed")
    assert key1 == key2, "Idempotency key must be deterministic for same inputs"


@pytest.mark.unit
def test_stable_idempotency_key_differs_by_reason() -> None:
    """Different reason → different key."""
    key1 = _stable_idempotency_key(_SESSION_ID, "verification_failed")
    key2 = _stable_idempotency_key(_SESSION_ID, "complaint")
    assert key1 != key2


@pytest.mark.unit
def test_stable_idempotency_key_differs_by_session() -> None:
    """Different session → different key."""
    key1 = _stable_idempotency_key("sess_A", "verification_failed")
    key2 = _stable_idempotency_key("sess_B", "verification_failed")
    assert key1 != key2


@pytest.mark.unit
def test_stable_idempotency_key_handles_none_session() -> None:
    """None session_id → key still deterministic (uses 'unknown' fallback)."""
    key1 = _stable_idempotency_key(None, "verification_failed")
    key2 = _stable_idempotency_key(None, "verification_failed")
    assert key1 == key2


# ---------------------------------------------------------------------------
# build_handoff_package unit tests (pure function)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_build_handoff_package_well_formed() -> None:
    """build_handoff_package produces a well-formed HandoffPackage."""
    draft = _minimal_draft()
    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=draft,
        current_turn_id="turn_001",
    )

    pkg = build_handoff_package(state, reason="Verification failed")

    assert pkg.reason == "Verification failed"
    assert pkg.draft_snapshot == draft
    assert "turn_001" in pkg.source_turn_ids


@pytest.mark.unit
def test_build_handoff_package_no_turn_id() -> None:
    """build_handoff_package with no current_turn_id produces empty source_turn_ids."""
    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=None,
        current_turn_id=None,
    )

    pkg = build_handoff_package(state, reason="Escalation")

    assert pkg.source_turn_ids == []
    assert pkg.draft_snapshot is None


@pytest.mark.unit
def test_build_handoff_package_validates_back() -> None:
    """HandoffPackage.model_dump can be re-validated back to HandoffPackage."""
    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=_minimal_draft(),
        current_turn_id="turn_002",
    )

    pkg = build_handoff_package(state, reason="Grant invalid")
    dumped = pkg.model_dump(mode="json")

    # Must round-trip cleanly
    restored = HandoffPackage.model_validate(dumped)
    assert restored.reason == pkg.reason
    assert restored.source_turn_ids == pkg.source_turn_ids


# ---------------------------------------------------------------------------
# human_handoff node integration tests (with fake tool client)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_human_handoff_well_formed_package() -> None:
    """human_handoff builds a well-formed HandoffPackage; terminal_state is set;
    handoff_package dict validates back to HandoffPackage."""
    call_log: list[dict] = []

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/handoffs":
            body = json.loads(req.content)
            call_log.append(body)
            return _json_response(200, _handoff_created_body(status="created"))
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=_minimal_draft(),
        current_turn_id="turn_hh_001",
        handoff_reason_code="verification_failed",
    )

    result = await human_handoff(state)

    # terminal_state must be set
    assert result["terminal_state"] == "human_handoff"

    # handoff_package must be present and validate back to HandoffPackage
    assert "handoff_package" in result
    pkg_dict = result["handoff_package"]
    restored_pkg = HandoffPackage.model_validate(pkg_dict)
    assert restored_pkg.reason != "", "reason must be non-empty"
    assert isinstance(restored_pkg.source_turn_ids, list)

    # handoff_result must be present (tool returned ok)
    assert "handoff_result" in result
    assert result["handoff_result"]["work_item_id"] == "wi_001"

    # Exactly one call to /api/handoffs
    assert len(call_log) == 1


@pytest.mark.unit
async def test_human_handoff_idempotency_key_stable() -> None:
    """Idempotency: same session+reason → same handoff_idempotency_key (Inv-9).

    Two calls with the same state produce the same key; the second call against
    a fake that returns 'existing' succeeds without error.
    """
    keys_seen: list[str] = []

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/handoffs":
            body = json.loads(req.content)
            keys_seen.append(body["handoff_idempotency_key"])
            # Second call returns "existing" — simulating Inv-9 dedup
            status = "created" if len(keys_seen) == 1 else "existing"
            return _json_response(200, _handoff_created_body(status=status))
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=None,
        handoff_reason_code="verification_failed",
    )

    result1 = await human_handoff(state)
    result2 = await human_handoff(state)

    # Both calls must produce the same idempotency key
    assert len(keys_seen) == 2
    assert keys_seen[0] == keys_seen[1], (
        "Inv-9: handoff_idempotency_key must be stable for same session+reason"
    )

    # Both calls must succeed
    assert result1["terminal_state"] == "human_handoff"
    assert result2["terminal_state"] == "human_handoff"

    # Second call returns existing status
    assert result2["handoff_result"]["status"] == "existing"


@pytest.mark.unit
async def test_human_handoff_terminal_state_set_even_when_tool_fails() -> None:
    """terminal_state is set even when request_human_handoff returns non-ok."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/handoffs":
            return _json_response(
                503,
                {
                    "ok": False,
                    "status": "routing_unavailable",
                    "correlation_id": "corr_hh_002",
                    "data": None,
                },
            )
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=None,
        handoff_reason_code="verification_failed",
    )

    result = await human_handoff(state)

    # terminal_state must still be set even on tool failure
    assert result["terminal_state"] == "human_handoff"
    assert "handoff_package" in result
    # handoff_result may be absent when tool fails
    assert result.get("handoff_result") is None


@pytest.mark.unit
async def test_human_handoff_draft_snapshot_preserved() -> None:
    """draft_snapshot in handoff_package matches the state's draft."""

    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/handoffs":
            return _json_response(200, _handoff_created_body())
        return _json_response(404, {"error": "not_found"})

    set_tool_client(_make_client(handler))

    draft = _minimal_draft()
    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id=_SESSION_ID,
        draft=draft,
        current_turn_id="turn_snap_001",
        handoff_reason_code="verification_failed",
    )

    result = await human_handoff(state)

    pkg_dict = result["handoff_package"]
    restored = HandoffPackage.model_validate(pkg_dict)

    # draft_snapshot must be preserved
    assert restored.draft_snapshot is not None
    assert restored.draft_snapshot.session_id == draft.session_id
    assert "turn_snap_001" in restored.source_turn_ids
