"""Unit tests for ToolClient envelope mapping (spec 03 §4.2, Inv-2/Inv-5/Inv-14/Inv-16).

All tests use ``httpx.MockTransport`` — NO real network is hit.

Tested invariants:
- Inv-5: PURE_READ tools retry exactly once on timeout (2 transport hits);
  DURABLE_MUTATION tools never retry (1 hit).
- Inv-2: operation_id / idempotency_key are injected VERBATIM from caller args.
- Inv-14: raw response body text never appears in ``customer_hint``.
- Inv-16: raw exception text never appears in ``customer_hint``.
- Happy path: 2xx → ``ok=True``, ``data`` carries the response body.
"""

from __future__ import annotations

import httpx
import pytest

from tests.unit_tests.tools.conftest import (
    CANNED_200,
    CANNED_202,
    CANNED_409,
    CANNED_429,
    CANNED_500,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _raw_body(response: httpx.Response) -> str:
    """Return the raw JSON body of a canned response as a string."""
    return response.content.decode()


# ---------------------------------------------------------------------------
# Non-2xx → ok=False with safe customer_hint (Inv-14)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_409_maps_to_conflict_envelope(client_factory) -> None:
    """A 409 response produces ok=False, status='conflict', safe customer_hint (Inv-14)."""
    client, captured = client_factory(CANNED_409)

    envelope = await client.call_tool("validate_booking_draft", "POST", "/validate")

    assert envelope.ok is False
    assert envelope.status == "conflict"
    assert envelope.customer_hint is not None
    # Raw body must NOT leak into the hint (Inv-14).
    raw = _raw_body(CANNED_409)
    assert raw not in envelope.customer_hint
    assert "already exists" not in (envelope.customer_hint or "")
    assert (
        "conflict" in envelope.customer_hint.lower() or len(envelope.customer_hint) > 0
    )


@pytest.mark.unit
async def test_429_maps_to_rate_limited_envelope(client_factory) -> None:
    """A 429 response produces ok=False, status='rate_limited', safe customer_hint (Inv-14)."""
    client, captured = client_factory(CANNED_429)

    envelope = await client.call_tool("get_catalog_item", "GET", "/catalog/item/1")

    assert envelope.ok is False
    assert envelope.status == "rate_limited"
    assert envelope.customer_hint is not None
    raw = _raw_body(CANNED_429)
    assert raw not in envelope.customer_hint
    assert "slow down" not in (envelope.customer_hint or "")


@pytest.mark.unit
async def test_500_maps_to_unavailable_envelope(client_factory) -> None:
    """A 500 response produces ok=False, status='unavailable', safe customer_hint (Inv-14)."""
    client, captured = client_factory(CANNED_500)

    envelope = await client.call_tool("search_vendor_catalog", "GET", "/catalog/search")

    assert envelope.ok is False
    assert envelope.status == "unavailable"
    assert envelope.customer_hint is not None
    raw = _raw_body(CANNED_500)
    assert raw not in envelope.customer_hint
    assert "internal_error" not in (envelope.customer_hint or "")
    assert "server error" not in (envelope.customer_hint or "")


# ---------------------------------------------------------------------------
# Inv-5: retry behaviour — PURE_READ retries once; DURABLE_MUTATION never.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_pure_read_timeout_retries_once(client_factory) -> None:
    """PURE_READ tool retries exactly once on timeout → 2 transport hits (Inv-5).

    The handler always raises ``httpx.ReadTimeout``.  The client retries once,
    still fails, and returns a degraded ``unavailable`` envelope.
    """
    timeout_exc = httpx.ReadTimeout("read timeout", request=None)
    client, captured = client_factory(timeout_exc)

    envelope = await client.call_tool(
        "validate_booking_draft",  # PURE_READ
        "POST",
        "/validate",
    )

    assert len(captured) == 2, (
        f"Expected 2 transport hits for PURE_READ, got {len(captured)}"
    )
    assert envelope.ok is False
    assert envelope.status == "unavailable"
    # Raw exception text must not leak (Inv-16).
    assert "read timeout" not in (envelope.customer_hint or "").lower()


@pytest.mark.unit
async def test_mutation_timeout_no_retry(client_factory) -> None:
    """DURABLE_MUTATION tool NEVER retries on timeout → 1 transport hit (Inv-5)."""
    timeout_exc = httpx.ReadTimeout("read timeout", request=None)
    client, captured = client_factory(timeout_exc)

    envelope = await client.call_tool(
        "submit_confirmed_intent",  # DURABLE_MUTATION
        "POST",
        "/booking-operations",
        operation_id="op_x",
        idempotency_key="idem_y",
    )

    assert len(captured) == 1, (
        f"Expected 1 transport hit for mutation, got {len(captured)}"
    )
    assert envelope.ok is False
    assert envelope.status == "unavailable"


@pytest.mark.unit
async def test_bounded_side_effect_timeout_no_retry(client_factory) -> None:
    """BOUNDED_SIDE_EFFECT tool NEVER retries on timeout → 1 transport hit (Inv-5)."""
    timeout_exc = httpx.ReadTimeout("read timeout", request=None)
    client, captured = client_factory(timeout_exc)

    envelope = await client.call_tool(
        "check_candidate_availability",  # BOUNDED_SIDE_EFFECT
        "POST",
        "/availability",
    )

    assert len(captured) == 1, (
        f"Expected 1 transport hit for bounded side-effect, got {len(captured)}"
    )
    assert envelope.ok is False
    assert envelope.status == "unavailable"


# ---------------------------------------------------------------------------
# Inv-2: operation_id / idempotency_key injected VERBATIM.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_mutation_headers_injected_verbatim(
    client_factory, resolved_tenant_context
) -> None:
    """Inv-2: X-Operation-Id and X-Idempotency-Key are set verbatim from caller args.

    Also asserts X-Correlation-Id and X-Tenant-Id are present.
    """
    client, captured = client_factory(CANNED_202)

    envelope = await client.call_tool(
        "submit_confirmed_intent",
        "POST",
        "/booking-operations",
        operation_id="op_x",
        idempotency_key="idem_y",
    )

    assert len(captured) == 1
    request = captured[0]
    assert request.headers["X-Operation-Id"] == "op_x", (
        f"Expected X-Operation-Id='op_x', got {request.headers.get('X-Operation-Id')!r}"
    )
    assert request.headers["X-Idempotency-Key"] == "idem_y", (
        f"Expected X-Idempotency-Key='idem_y', got {request.headers.get('X-Idempotency-Key')!r}"
    )
    assert "X-Correlation-Id" in request.headers
    assert "X-Tenant-Id" in request.headers
    assert request.headers["X-Tenant-Id"] == resolved_tenant_context.tenant_id
    assert request.headers["X-Correlation-Id"] == resolved_tenant_context.correlation_id

    assert envelope.ok is True


# ---------------------------------------------------------------------------
# 2xx happy path → ok=True, data carries body.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_2xx_happy_path(client_factory) -> None:
    """A 200 response produces ok=True and data carries the response body dict."""
    client, captured = client_factory(CANNED_200)

    envelope = await client.call_tool(
        "find_customer_context",
        "GET",
        "/customers/ctx",
    )

    assert envelope.ok is True
    assert envelope.data is not None
    assert envelope.data.get("status") == "ok"
    assert len(captured) == 1


@pytest.mark.unit
async def test_202_accepted_happy_path(client_factory) -> None:
    """A 202 response is treated as 2xx success → ok=True."""
    client, captured = client_factory(CANNED_202)

    envelope = await client.call_tool(
        "submit_confirmed_intent",
        "POST",
        "/booking-operations",
        operation_id="op_abc",
        idempotency_key="idem_abc",
    )

    assert envelope.ok is True
    assert envelope.status == "accepted"
    assert len(captured) == 1


# ---------------------------------------------------------------------------
# Correlation ID propagation.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_correlation_id_propagated_from_tenant_context(
    client_factory, resolved_tenant_context
) -> None:
    """The envelope's correlation_id matches the tenant context's correlation_id."""
    client, captured = client_factory(CANNED_200)

    envelope = await client.call_tool("get_catalog_item", "GET", "/catalog/1")

    assert envelope.correlation_id == resolved_tenant_context.correlation_id
