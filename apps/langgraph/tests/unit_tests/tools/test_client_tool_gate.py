"""Unit tests for the Inv-12 tool gate (gate 1c) in ToolClient — spec 14 §8.11/§27.1.

All tests use ``httpx.MockTransport`` — NO real network is hit.

Tested invariants:
- An unknown (non-canonical) tool name returns a ``tool_not_permitted``
  envelope WITHOUT touching the transport (it used to raise a raw KeyError).
- The unknown-name check fires even before the Inv-13 tenant gate.
- An installed ``ToolAllowlist`` narrows further: a canonical tool the
  allowlist denies also returns ``tool_not_permitted`` pre-transport.
- With NO allowlist installed, every canonical tool passes the gate (data
  tools then fall through to the tenant gate as before — unchanged behaviour).
"""

from __future__ import annotations

import pytest

from cordia_agent.safety.injection_harness import ToolAllowlist
from cordia_agent.tools._timeouts import ALL_TOOLS
from tests.unit_tests.tools.conftest import CANNED_200


# ---------------------------------------------------------------------------
# Unknown tool name → tool_not_permitted, transport NOT hit.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_unknown_tool_returns_tool_not_permitted_envelope(
    client_factory,
) -> None:
    """A non-canonical tool name → typed envelope, never a KeyError."""
    client, captured = client_factory(CANNED_200)

    envelope = await client.call_tool("run_shell_command", "POST", "/anything")

    assert len(captured) == 0, "gate 1c violated: transport was hit for an unknown tool"
    assert envelope.ok is False
    assert envelope.status == "tool_not_permitted"
    assert envelope.customer_hint is not None
    assert len(envelope.customer_hint) > 0


@pytest.mark.unit
async def test_unknown_tool_gate_fires_before_tenant_gate(client_factory) -> None:
    """With NO tenant context, an unknown tool is still tool_not_permitted.

    The tool-surface decision outranks tenant resolution — an off-surface name
    must never be reported as a tenant problem.
    """
    client, captured = client_factory(CANNED_200, tenant_context=None)

    envelope = await client.call_tool("publish_vendor_catalog", "POST", "/menu")

    assert len(captured) == 0
    assert envelope.status == "tool_not_permitted"


# ---------------------------------------------------------------------------
# Installed allowlist narrows the canonical surface further.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_allowlist_denies_canonical_tool_not_on_it(client_factory) -> None:
    """set_tool_allowlist narrows: canonical-but-denied → tool_not_permitted."""
    client, captured = client_factory(CANNED_200)
    client.set_tool_allowlist(ToolAllowlist(["get_tenant_context"]))

    envelope = await client.call_tool("submit_confirmed_intent", "POST", "/ops")

    assert len(captured) == 0
    assert envelope.ok is False
    assert envelope.status == "tool_not_permitted"


@pytest.mark.unit
async def test_allowlist_permits_tool_on_it(client_factory) -> None:
    """An allowlisted canonical tool passes the gate and reaches the transport."""
    client, captured = client_factory(CANNED_200)
    client.set_tool_allowlist(ToolAllowlist(["validate_booking_draft"]))

    envelope = await client.call_tool("validate_booking_draft", "POST", "/validate")

    assert len(captured) == 1
    assert envelope.ok is True


# ---------------------------------------------------------------------------
# No allowlist installed → all canonical tools (21) pass the gate unchanged.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_all_canonical_tools_pass_gate_when_no_allowlist_set(
    client_factory,
) -> None:
    """Without set_tool_allowlist, no canonical tool is ever tool_not_permitted.

    Run without tenant context so no transport call is needed: data tools fall
    through to the tenant gate (``tenant_not_resolved``) exactly as before —
    proving the new gate is a strict no-op for the existing surface.
    """
    client, _captured = client_factory(CANNED_200, tenant_context=None)

    for tool_name in sorted(ALL_TOOLS):
        envelope = await client.call_tool(tool_name, "GET", "/probe")
        assert envelope.status != "tool_not_permitted", (
            f"gate 1c regression: canonical tool {tool_name!r} was refused "
            "with no allowlist installed"
        )
