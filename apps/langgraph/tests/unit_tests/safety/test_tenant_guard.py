"""Unit tests for the tenant-context guard (resolve_tenant node, Inv-13).

Fixtures register the stub verifier before each test and clear it after,
keeping tests fully isolated.
"""

from __future__ import annotations

import pytest

from cordia_agent.nodes.resolve_tenant import resolve_tenant
from cordia_agent.safety.tenant_context import (
    InvalidTenantContext,
    clear_verifier,
    register_verifier,
)
from cordia_agent.state import AgentState, Channel
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
    "allowed_policy_ids": ["policy_a", "policy_b"],
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
    assert result["allowed_policy_ids"] == ["policy_a", "policy_b"]
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
    benign_payload, _benign_sig = benign_token.rsplit(".", 1)
    evil_payload, _evil_sig = evil_token.rsplit(".", 1)

    # Evil payload + benign signature: signature is valid for benign_payload only.
    spliced = f"{evil_payload}.{_benign_sig}"
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
