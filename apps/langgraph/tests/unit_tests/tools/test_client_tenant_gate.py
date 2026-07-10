"""Unit tests for the Inv-13 tenant-context gate in ToolClient.

All tests use ``httpx.MockTransport`` — NO real network is hit.

Tested invariants:
- Inv-13: data tools with no resolved tenant context return a
  ``tenant_not_resolved`` envelope WITHOUT touching the transport
  (``len(captured) == 0``).
- get_tenant_context is EXEMPT from the gate — it DOES hit the transport
  even when no context has been set.
- Table coverage: ``set(TOOL_DEADLINES) == set(TOOL_SIDE_EFFECT_CLASS) == ALL_TOOLS``
  and ``len(ALL_TOOLS) == 21``.
"""

from __future__ import annotations

import pytest

from cordia_agent.tools._timeouts import (
    ALL_TOOLS,
    TOOL_DEADLINES,
    TOOL_SIDE_EFFECT_CLASS,
)
from tests.unit_tests.tools.conftest import CANNED_200


# ---------------------------------------------------------------------------
# Inv-13: data tool with no tenant context → envelope, transport NOT hit.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_data_tool_no_context_returns_tenant_not_resolved_envelope(
    client_factory,
) -> None:
    """Inv-13: validate_booking_draft with tenant_context=None → tenant_not_resolved.

    The transport must NOT be hit (``len(captured) == 0``).
    """
    client, captured = client_factory(CANNED_200, tenant_context=None)

    envelope = await client.call_tool(
        "validate_booking_draft",
        "POST",
        "/validate",
    )

    assert len(captured) == 0, (
        f"Inv-13 violated: transport was hit {len(captured)} time(s) before "
        "tenant context was resolved."
    )
    assert envelope.ok is False
    assert envelope.status == "tenant_not_resolved"
    assert envelope.customer_hint is not None
    assert len(envelope.customer_hint) > 0


@pytest.mark.unit
async def test_mutation_tool_no_context_returns_tenant_not_resolved_envelope(
    client_factory,
) -> None:
    """Inv-13: submit_confirmed_intent with tenant_context=None → tenant_not_resolved.

    Mutation tools are also subject to the gate (only get_tenant_context is exempt).
    """
    client, captured = client_factory(CANNED_200, tenant_context=None)

    envelope = await client.call_tool(
        "submit_confirmed_intent",
        "POST",
        "/booking-operations",
        operation_id="op_1",
        idempotency_key="idem_1",
    )

    assert len(captured) == 0, (
        f"Inv-13 violated: mutation tool hit transport {len(captured)} time(s) "
        "without resolved tenant context."
    )
    assert envelope.ok is False
    assert envelope.status == "tenant_not_resolved"


@pytest.mark.unit
async def test_find_manageable_bookings_no_context_returns_gate_envelope(
    client_factory,
) -> None:
    """Inv-13: find_manageable_bookings with tenant_context=None → tenant_not_resolved."""
    client, captured = client_factory(CANNED_200, tenant_context=None)

    envelope = await client.call_tool(
        "find_manageable_bookings",
        "GET",
        "/bookings",
    )

    assert len(captured) == 0
    assert envelope.ok is False
    assert envelope.status == "tenant_not_resolved"


# ---------------------------------------------------------------------------
# get_tenant_context is EXEMPT: it DOES hit the transport with no context.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_get_tenant_context_exempt_from_gate(client_factory) -> None:
    """get_tenant_context is the resolver tool — exempt from Inv-13 gate.

    With ``tenant_context=None``, the transport MUST still be hit so the
    resolver can fetch the context.
    """
    client, captured = client_factory(CANNED_200, tenant_context=None)

    envelope = await client.call_tool(
        "get_tenant_context",
        "GET",
        "/tenant/context",
    )

    assert len(captured) == 1, (
        f"get_tenant_context should have hit the transport once, got {len(captured)}"
    )
    assert envelope.ok is True


# ---------------------------------------------------------------------------
# Table coverage: 21 tools (17 spec-03 + 2 spec-14 consent + 1 spec-14 transcript + 1 spec-13 trace).
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_all_tools_count_is_21() -> None:
    """ALL_TOOLS must contain exactly 19 tools (spec 03 §6.6 + spec 14 §5.1)."""
    assert len(ALL_TOOLS) == 21, (
        f"Expected 19 tools in ALL_TOOLS, found {len(ALL_TOOLS)}: {sorted(ALL_TOOLS)}"
    )


@pytest.mark.unit
def test_deadline_and_side_effect_class_tables_cover_all_tools() -> None:
    """TOOL_DEADLINES and TOOL_SIDE_EFFECT_CLASS must have identical key sets == ALL_TOOLS."""
    assert set(TOOL_DEADLINES) == set(ALL_TOOLS), (
        f"TOOL_DEADLINES keys differ from ALL_TOOLS.\n"
        f"  Extra in DEADLINES: {set(TOOL_DEADLINES) - set(ALL_TOOLS)}\n"
        f"  Missing from DEADLINES: {set(ALL_TOOLS) - set(TOOL_DEADLINES)}"
    )
    assert set(TOOL_SIDE_EFFECT_CLASS) == set(ALL_TOOLS), (
        f"TOOL_SIDE_EFFECT_CLASS keys differ from ALL_TOOLS.\n"
        f"  Extra in CLASS: {set(TOOL_SIDE_EFFECT_CLASS) - set(ALL_TOOLS)}\n"
        f"  Missing from CLASS: {set(ALL_TOOLS) - set(TOOL_SIDE_EFFECT_CLASS)}"
    )


@pytest.mark.unit
def test_all_tools_is_frozenset() -> None:
    """ALL_TOOLS is a frozenset (immutable, hashable, membership-checkable)."""
    assert isinstance(ALL_TOOLS, frozenset)


@pytest.mark.unit
def test_known_tool_names_present() -> None:
    """Spot-check that key tool names are registered in ALL_TOOLS."""
    expected_tools = {
        "get_tenant_context",
        "submit_confirmed_intent",
        "submit_booking_modification",
        "submit_booking_cancellation",
        "validate_booking_draft",
        "check_candidate_availability",
        "request_verification",
        "get_booking_operation_status",
        "resolve_consent_policy",
        "record_consent",
    }
    missing = expected_tools - ALL_TOOLS
    assert not missing, f"Expected tool names missing from ALL_TOOLS: {missing}"
