"""Unit tests for the tenant-context guard (resolve_tenant node, Inv-13).

Fixtures register the stub verifier before each test and clear it after,
keeping tests fully isolated.
"""

from __future__ import annotations

import base64
import json

import httpx
import pytest

from cordia_agent.nodes.resolve_tenant import resolve_tenant
from cordia_agent.safety.tenant_context import (
    InvalidTenantContext,
    clear_verifier,
    register_verifier,
)
from cordia_agent.state import AgentState, Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from tests._stubs.tenant_context_stub import StubTenantContextVerifier, sign


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_VALID_CLAIMS: dict = {
    "tenant_id": "tenant_abc",
    "channel": Channel.voice.value,
    "channel_endpoint_id": "ep_001",
    "configuration_version": "v2",
    "correlation_id": "corr_xyz",
    "session_id": "sess_001",
    "allowed_agent_policy_ids": ["agent_policy_a"],
    "allowed_tool_policy_ids": ["policy_a", "policy_b"],
    "issued_at": "2026-07-25T00:00:00Z",
    "expires_at": "2026-07-25T01:00:00Z",
}


def _make_state(signed: str | None) -> AgentState:
    return AgentState(signed_tenant_context=signed)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _register_stub_verifier():
    """Register the stub verifier before each test; clear after."""
    register_verifier(StubTenantContextVerifier())
    yield
    clear_verifier()


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_resolve_tenant_valid_token_populates_state() -> None:
    """A validly-signed token populates AgentState with the verified tenant fields."""
    token = sign(_VALID_CLAIMS)
    state = _make_state(token)

    result = await resolve_tenant(state)

    assert result["tenant_id"] == "tenant_abc"
    assert result["tenant_policy_version"] == "v2"
    assert result["correlation_id"] == "corr_xyz"
    assert result["channel_endpoint_id"] == "ep_001"
    assert result["session_id"] == "sess_001"
    assert result["allowed_agent_policy_ids"] == ["agent_policy_a"]
    assert result["allowed_tool_policy_ids"] == ["policy_a", "policy_b"]
    assert result["tenant_context_verified"] is True


@pytest.mark.unit
async def test_resolve_tenant_none_token_raises() -> None:
    """A missing signed_tenant_context raises InvalidTenantContext (graph gate, Inv-13)."""
    state = _make_state(None)

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(state)


@pytest.mark.unit
async def test_resolve_tenant_empty_token_raises() -> None:
    """An empty-string signed_tenant_context raises InvalidTenantContext."""
    state = _make_state("")

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(state)


@pytest.mark.unit
async def test_resolve_tenant_forged_token_raises() -> None:
    """Inv-13 core: a tampered token (claimed tenant_id differs) is rejected.

    The forged token has a different tenant_id than the one that was signed,
    so the HMAC does not match and the verifier raises InvalidTenantContext.
    """
    # Forge: produce a token with a tampered tenant_id signed under a different
    # secret — the stub verifier will reject it because the HMAC doesn't match.
    forged_claims = dict(_VALID_CLAIMS, tenant_id="evil_tenant")
    forged_token = sign(forged_claims, secret="wrong-secret")

    state = _make_state(forged_token)

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(state)


@pytest.mark.unit
async def test_resolve_tenant_same_secret_payload_swap_rejected() -> None:
    """Inv-13 sharp proof: the signature is bound to the payload (covers tenant_id).

    Take a validly-signed benign token and a validly-signed evil token (both under
    the SAME, correct secret), then splice the evil payload onto the benign
    signature.  Because the HMAC covers the payload, the spliced token's signature
    no longer matches the evil payload and the verifier rejects it — proving an
    attacker who cannot forge the secret still cannot swap the tenant_id.
    """
    benign_token = sign(_VALID_CLAIMS)  # default (correct) secret
    evil_token = sign(dict(_VALID_CLAIMS, tenant_id="evil_tenant"))  # same secret
    _benign_header, benign_payload, benign_sig = benign_token.split(".")
    evil_header, evil_payload, _evil_sig = evil_token.split(".")

    # Splice the evil header+payload onto the benign signature. Because the HMAC
    # covers the JWS signing input (header + "." + payload), the benign signature
    # no longer matches the evil payload and the verifier rejects it.
    spliced = f"{evil_header}.{evil_payload}.{benign_sig}"
    assert evil_payload != benign_payload  # the swap actually changed the payload

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(_make_state(spliced))


@pytest.mark.unit
async def test_resolve_tenant_garbage_token_raises() -> None:
    """Garbage / non-token strings raise InvalidTenantContext."""
    state = _make_state("not.a.valid.token.at.all")

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(state)


@pytest.mark.unit
async def test_resolve_tenant_ignores_state_tenant_id() -> None:
    """Inv-13 isolation: a tenant_id in AgentState is NOT used — only the verified context wins.

    We pre-populate AgentState.tenant_id with a different value.  After
    resolve_tenant succeeds, the result must carry the verified tenant_id from
    the signed context, not the pre-populated one.
    """
    token = sign(_VALID_CLAIMS)
    # Pre-populate with a different (would-be bypass) tenant_id.
    state = AgentState(signed_tenant_context=token, tenant_id="unverified_evil_tenant")

    result = await resolve_tenant(state)

    # The result must use only the verified context's tenant_id.
    assert result["tenant_id"] == "tenant_abc"
    assert result["tenant_context_verified"] is True


@pytest.mark.unit
def test_stub_emits_three_part_jws_with_expected_header() -> None:
    """The migrated stub emits a 3-part compact JWS whose header is tenantctx+jws.

    Guards the Phase-1 migration from the old 2-part (payload.signature) form to
    the real 3-part (header.payload.signature) JWS the C# signer (Phase 2) and
    production verifier (Phase 5) must byte-match.
    """
    token = sign(_VALID_CLAIMS, kid="kid_test")
    parts = token.split(".")
    assert len(parts) == 3, "token must be header.payload.signature (3-part JWS)"

    header_b64 = parts[0]
    padding = "=" * (-len(header_b64) % 4)
    header = json.loads(base64.urlsafe_b64decode(header_b64 + padding))
    assert header == {"alg": "HS256", "kid": "kid_test", "typ": "tenantctx+jws"}


# ---------------------------------------------------------------------------
# Fix A — resolve_tenant injects verified ctx into ToolClient (Inv-13 gate)
# ---------------------------------------------------------------------------


def _make_200_transport() -> httpx.MockTransport:
    """Return a MockTransport that answers every request with 200 + minimal JSON."""

    def _handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            headers={"content-type": "application/json"},
            content=json.dumps({"status": "ok", "ok": True}).encode(),
        )

    return httpx.MockTransport(_handler)


@pytest.fixture()
def _reset_tool_client():
    """Ensure the tool client is cleared after each test in this block."""
    yield
    reset_tool_client()


@pytest.mark.unit
async def test_resolve_tenant_injects_ctx_so_tool_calls_pass_inv13_gate(
    _reset_tool_client,
) -> None:
    """Fix A: after resolve_tenant, a ToolClient call MUST NOT return tenant_not_resolved.

    Registers a real ToolClient backed by MockTransport BEFORE calling
    resolve_tenant.  The node injects the verified TenantContext via
    ``set_tenant_context``; afterwards, ``call_tool`` routes past the Inv-13
    gate and hits the transport (returns ok, not tenant_not_resolved).
    """
    transport = _make_200_transport()
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    # No tenant context yet — gate would block any call at this point.
    client = ToolClient(http, tenant_context=None)
    set_tool_client(client)

    token = sign(_VALID_CLAIMS)
    state = _make_state(token)

    # Run resolve_tenant — it must inject the verified ctx into the client.
    await resolve_tenant(state)

    # Now a data-tool call must pass the Inv-13 gate (status != "tenant_not_resolved").
    result = await client.call_tool(
        "find_customer_context", "GET", "/api/customers/context"
    )
    assert result.status != "tenant_not_resolved", (
        "Fix A: after resolve_tenant, call_tool must NOT return tenant_not_resolved"
    )


@pytest.mark.unit
async def test_tool_call_returns_tenant_not_resolved_without_resolve_tenant(
    _reset_tool_client,
) -> None:
    """Control: a ToolClient with NO tenant context DOES return tenant_not_resolved.

    This confirms the gate is live and the previous test is not a false positive.
    """
    transport = _make_200_transport()
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    client = ToolClient(http, tenant_context=None)
    set_tool_client(client)

    # No resolve_tenant call — context is never injected.
    result = await client.call_tool(
        "find_customer_context", "GET", "/api/customers/context"
    )
    assert result.status == "tenant_not_resolved", (
        "Control: without resolve_tenant the gate must block and return tenant_not_resolved"
    )
