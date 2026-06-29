"""Unit tests for the load_context node (02-H rewrite, §8.2 / §9.2).

Uses ``httpx.MockTransport`` + ``set_tool_client`` to exercise the node against
a fake API (same pattern as the 03-E integration tests).

Covers:
  - Tenant policy_version is mapped from the tool response.
  - Customer context (customer_id, display_name) is mapped correctly.
  - ``recent_booking_summaries`` is ALWAYS empty (§9.2 — discovery projection
    is minimized; sensitive history never crosses the 03 boundary here).
  - Non-ok tenant envelope → node continues gracefully (no crash).
  - Non-ok customer envelope → node continues gracefully (no crash).
  - The result dict always has the expected keys on success.
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from cordia_agent.nodes.load_context import load_context
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import AgentState, Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


# ---------------------------------------------------------------------------
# Fake API helpers
# ---------------------------------------------------------------------------


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_lc_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_lc_001",
    configuration_version="v1",
    correlation_id="corr_lc_test_001",
    allowed_policy_ids=["policy_a"],
)

_TENANT_CTX_OK_BODY = {
    "status": "ok",
    "tenant_id": "tenant_lc_test",
    "policy_version": "v2-lc",
    "timezone": "Australia/Sydney",
    "supported_operations": ["create"],
    "capabilities": [],
    "currency": "AUD",
}

_CUSTOMER_CTX_OK_BODY = {
    "status": "ok",
    "customer_id": "cust_lc_001",
    "display_name": "Alice Testuser",
    "has_tokenized_payment": False,
}


def _make_happy_handler(request: httpx.Request) -> httpx.Response:
    """Handles both /api/tenant-context and /api/customers/context with 2xx."""
    path = request.url.path
    if path == "/api/tenant-context":
        return _json_response(200, _TENANT_CTX_OK_BODY)
    if path == "/api/customers/context":
        return _json_response(200, _CUSTOMER_CTX_OK_BODY)
    return _json_response(404, {"error": "not_found"})


def _make_tenant_error_handler(request: httpx.Request) -> httpx.Response:
    """Returns 500 for tenant-context, 2xx for customers/context."""
    path = request.url.path
    if path == "/api/tenant-context":
        return _json_response(500, {"error": "server_error"})
    if path == "/api/customers/context":
        return _json_response(200, _CUSTOMER_CTX_OK_BODY)
    return _json_response(404, {"error": "not_found"})


def _make_customer_error_handler(request: httpx.Request) -> httpx.Response:
    """Returns 2xx for tenant-context, 500 for customers/context."""
    path = request.url.path
    if path == "/api/tenant-context":
        return _json_response(200, _TENANT_CTX_OK_BODY)
    if path == "/api/customers/context":
        return _json_response(500, {"error": "server_error"})
    return _json_response(404, {"error": "not_found"})


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_tool_client():
    """Always reset the tool client after each test."""
    yield
    reset_tool_client()


# ---------------------------------------------------------------------------
# Tests — happy path
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_load_context_maps_tenant_policy_version() -> None:
    """load_context maps tenant_policy_version from the tool response."""
    set_tool_client(_make_client(_make_happy_handler))
    state = AgentState(tenant_id="tenant_lc_test", session_id="session_lc_1")

    result = await load_context(state)

    assert result.get("tenant_policy_version") == "v2-lc"


@pytest.mark.unit
async def test_load_context_maps_customer_context() -> None:
    """load_context maps customer_id and display_name from the tool response."""
    set_tool_client(_make_client(_make_happy_handler))
    state = AgentState(tenant_id="tenant_lc_test", session_id="session_lc_1")

    result = await load_context(state)

    ctx = result.get("customer_context")
    assert ctx is not None
    assert ctx.customer_id == "cust_lc_001"
    assert ctx.display_name == "Alice Testuser"


@pytest.mark.unit
async def test_load_context_recent_booking_summaries_always_empty() -> None:
    """§9.2: recent_booking_summaries is ALWAYS empty — discovery projection is minimized.

    The 03 CustomerContextView does not include sensitive history.  Sensitive
    history is only fetched after a verification grant is set (spec 03 §9.2).
    """
    set_tool_client(_make_client(_make_happy_handler))
    state = AgentState(
        tenant_id="tenant_lc_test",
        session_id="session_lc_1",
        verification_grant_id="grant_xyz",  # even WITH a grant, no sensitive history here
    )

    result = await load_context(state)

    ctx = result.get("customer_context")
    assert ctx is not None
    assert ctx.recent_booking_summaries == [], (
        "§9.2: discovery projection never includes sensitive history; "
        "recent_booking_summaries must always be empty from load_context"
    )


@pytest.mark.unit
async def test_load_context_result_contains_expected_keys() -> None:
    """load_context result dict contains 'customer_context' (and optionally 'tenant_policy_version')."""
    set_tool_client(_make_client(_make_happy_handler))
    state = AgentState(tenant_id="tenant_lc_test", session_id="session_lc_1")

    result = await load_context(state)

    assert "customer_context" in result


@pytest.mark.unit
async def test_load_context_skips_policy_version_when_already_set() -> None:
    """load_context does NOT overwrite tenant_policy_version when already set on state."""
    set_tool_client(_make_client(_make_happy_handler))
    state = AgentState(
        tenant_id="tenant_lc_test",
        session_id="session_lc_1",
        tenant_policy_version="v1-existing",  # already set
    )

    result = await load_context(state)

    # tenant_policy_version must NOT be in the result (already set on state)
    assert "tenant_policy_version" not in result


# ---------------------------------------------------------------------------
# Tests — graceful degraded paths (non-ok envelopes)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_load_context_continues_on_tenant_error() -> None:
    """Non-ok tenant envelope → node continues without crashing; no policy_version set."""
    set_tool_client(_make_client(_make_tenant_error_handler))
    state = AgentState(tenant_id="tenant_lc_test", session_id="session_lc_1")

    result = await load_context(state)

    # Policy version not set due to error
    assert "tenant_policy_version" not in result
    # Customer context may still be loaded
    # (this asserts the node did not crash)
    assert isinstance(result, dict)


@pytest.mark.unit
async def test_load_context_continues_on_customer_error() -> None:
    """Non-ok customer envelope → node continues; no customer_context key set."""
    set_tool_client(_make_client(_make_customer_error_handler))
    state = AgentState(tenant_id="tenant_lc_test", session_id="session_lc_1")

    result = await load_context(state)

    # Tenant policy_version should have been set
    assert result.get("tenant_policy_version") == "v2-lc"
    # Customer context not present due to error
    assert "customer_context" not in result
