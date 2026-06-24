"""Unit tests for the load_context node (02-F, §8.2 / §9.2).

Covers:
  - Sensitive history (recent_booking_summaries) is DROPPED when
    verification_grant_id is None (§9.2 minimization rule).
  - Sensitive history is KEPT when verification_grant_id is set.
"""

from __future__ import annotations

import pytest

from cordia_agent.nodes.load_context import (
    clear_read_client,
    load_context,
    register_read_client,
)
from cordia_agent.state import AgentState
from tests._stubs.read_tools_fake import FakeReadContextClient


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _register_fake_client():
    """Register FakeReadContextClient before each test; clear after."""
    register_read_client(FakeReadContextClient())
    yield
    clear_read_client()


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_load_context_drops_sensitive_history_without_grant() -> None:
    """Without a verification grant, recent_booking_summaries is cleared (§9.2)."""
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        verification_grant_id=None,  # no grant
    )

    result = await load_context(state)

    customer_context = result["customer_context"]
    assert customer_context is not None
    assert customer_context.recent_booking_summaries == [], (
        "Sensitive history must be empty without a verification grant (§9.2)"
    )


@pytest.mark.unit
async def test_load_context_keeps_sensitive_history_with_grant() -> None:
    """With a verification grant, recent_booking_summaries is retained (§9.2 positive path)."""
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        verification_grant_id="grant_xyz",  # grant present
    )

    result = await load_context(state)

    customer_context = result["customer_context"]
    assert customer_context is not None
    assert len(customer_context.recent_booking_summaries) > 0, (
        "Sensitive history must be present when a verification grant is set (§9.2)"
    )


@pytest.mark.unit
async def test_load_context_non_sensitive_fields_always_present() -> None:
    """Non-sensitive fields (customer_id, display_name) are always present."""
    state = AgentState(
        tenant_id="tenant_a",
        session_id="session_1",
        verification_grant_id=None,
    )

    result = await load_context(state)

    customer_context = result["customer_context"]
    # The fake always provides customer_id and display_name.
    assert customer_context.customer_id == "cust_fake_001"
    assert customer_context.display_name == "Test Customer"


@pytest.mark.unit
async def test_load_context_returns_customer_context_key() -> None:
    """load_context result dict must contain 'customer_context'."""
    state = AgentState(tenant_id="tenant_a", session_id="session_1")
    result = await load_context(state)
    assert "customer_context" in result
