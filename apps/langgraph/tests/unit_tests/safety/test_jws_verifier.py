"""Unit tests for the production HS256 JWS verifier (spec-15 Phase 5).

Covers the full verification order (round-trip, tampered payload, wrong secret,
unknown kid, expired, future-issued, wrong typ, wrong alg, malformed), the
config loader + startup registration, and drives the ``resolve_tenant`` node with
the PRODUCTION verifier registered.

The suite also pins the shared CROSS-IMPL VECTOR (secret + kid + exact token)
that the Phase 2 C# ``JwsTenantContextSignerTests`` asserts against, proving both
implementations agree byte-for-byte on the same token.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
from datetime import UTC, datetime

import pytest

from cordia_agent.nodes.resolve_tenant import resolve_tenant
from cordia_agent.safety.jws_verifier import (
    SIGNING_KEYS_ENV_VAR,
    JwsTenantContextVerifier,
    bootstrap_verifier,
    keys_from_config,
)
from cordia_agent.safety.tenant_context import (
    InvalidTenantContext,
    TenantChannel,
    TenantContext,
    clear_verifier,
    get_verifier,
    register_verifier,
)
from cordia_agent.state import AgentState
from tests._stubs.tenant_context_stub import sign


# ---------------------------------------------------------------------------
# Constants / helpers
# ---------------------------------------------------------------------------

_SECRET = "unit-test-secret-abcdef"
_KID = "unit-kid-1"
# A clock pinned inside the [issued_at, expires_at] window of _CLAIMS below.
_PINNED_NOW = datetime(2026, 7, 25, 0, 30, 0, tzinfo=UTC)

_CLAIMS: dict = {
    "tenant_id": "tenant_abc",
    "channel": "voice",
    "channel_endpoint_id": "ep_001",
    "configuration_version": "v2",
    "correlation_id": "corr_xyz",
    "session_id": "sess_001",
    "allowed_agent_policy_ids": ["agent_policy_a"],
    "allowed_tool_policy_ids": ["tool_policy_a", "tool_policy_b"],
    "issued_at": "2026-07-25T00:00:00Z",
    "expires_at": "2026-07-25T01:00:00Z",
}


def _b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


def _canonical(obj: dict) -> bytes:
    return json.dumps(
        obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    ).encode()


def _sign_with_header(claims: dict, header: dict, secret: str) -> str:
    """Mint a 3-part JWS with an arbitrary *header* (to exercise alg/typ checks)."""
    header_b64 = _b64url(_canonical(header))
    payload_b64 = _b64url(_canonical(claims))
    signing_input = f"{header_b64}.{payload_b64}".encode()
    sig_b64 = _b64url(hmac.new(secret.encode(), signing_input, hashlib.sha256).digest())
    return f"{header_b64}.{payload_b64}.{sig_b64}"


def _verifier(**overrides) -> JwsTenantContextVerifier:
    """A verifier holding the unit key set with the clock pinned inside the window."""
    return JwsTenantContextVerifier(
        {_KID: _SECRET}, now=lambda: _PINNED_NOW, **overrides
    )


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _isolate_verifier():
    """Clear any registered verifier after each test (registry is process-global)."""
    yield
    clear_verifier()


# ---------------------------------------------------------------------------
# Round-trip + failure modes
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_roundtrip_stub_token_verifies() -> None:
    """A real 3-part JWS from the stub sign() verifies to the expected TenantContext."""
    token = sign(_CLAIMS, secret=_SECRET, kid=_KID)

    ctx = _verifier().verify(token)

    assert isinstance(ctx, TenantContext)
    assert ctx.tenant_id == "tenant_abc"
    assert ctx.channel is TenantChannel.voice
    assert ctx.channel_endpoint_id == "ep_001"
    assert ctx.configuration_version == "v2"
    assert ctx.correlation_id == "corr_xyz"
    assert ctx.session_id == "sess_001"
    assert ctx.allowed_agent_policy_ids == ["agent_policy_a"]
    assert ctx.allowed_tool_policy_ids == ["tool_policy_a", "tool_policy_b"]


@pytest.mark.unit
def test_tampered_payload_rejected() -> None:
    """Flipping a byte in the payload segment breaks the HMAC → InvalidTenantContext."""
    header_b64, payload_b64, sig_b64 = sign(_CLAIMS, secret=_SECRET, kid=_KID).split(
        "."
    )
    # Mutate one character of the payload (keeps it valid base64url).
    swapped = "A" if payload_b64[5] != "A" else "B"
    tampered_payload = payload_b64[:5] + swapped + payload_b64[6:]
    tampered = f"{header_b64}.{tampered_payload}.{sig_b64}"

    with pytest.raises(InvalidTenantContext):
        _verifier().verify(tampered)


@pytest.mark.unit
def test_wrong_secret_rejected() -> None:
    """A token signed under a different secret (same kid) fails the HMAC check."""
    token = sign(_CLAIMS, secret="a-completely-different-secret", kid=_KID)

    with pytest.raises(InvalidTenantContext):
        _verifier().verify(token)


@pytest.mark.unit
def test_unknown_kid_rejected() -> None:
    """A kid not in the active-key set is rejected (retired / unknown key)."""
    token = sign(_CLAIMS, secret=_SECRET, kid="some-other-kid")

    with pytest.raises(InvalidTenantContext):
        _verifier().verify(token)


@pytest.mark.unit
def test_expired_token_rejected() -> None:
    """A token whose expires_at has passed (beyond skew) is rejected."""
    token = sign(_CLAIMS, secret=_SECRET, kid=_KID)
    after_expiry = datetime(2026, 7, 25, 2, 0, 0, tzinfo=UTC)

    verifier = JwsTenantContextVerifier({_KID: _SECRET}, now=lambda: after_expiry)
    with pytest.raises(InvalidTenantContext):
        verifier.verify(token)


@pytest.mark.unit
def test_future_issued_token_rejected() -> None:
    """A token whose issued_at is in the future (beyond skew) is rejected."""
    token = sign(_CLAIMS, secret=_SECRET, kid=_KID)
    before_issue = datetime(2026, 7, 24, 0, 0, 0, tzinfo=UTC)

    verifier = JwsTenantContextVerifier({_KID: _SECRET}, now=lambda: before_issue)
    with pytest.raises(InvalidTenantContext):
        verifier.verify(token)


@pytest.mark.unit
def test_within_skew_accepted() -> None:
    """Expiry/issue checks allow the configured clock skew (default 60s)."""
    token = sign(_CLAIMS, secret=_SECRET, kid=_KID)
    # 30s past expires_at — inside the 60s skew window → still accepted.
    just_after = datetime(2026, 7, 25, 1, 0, 30, tzinfo=UTC)

    verifier = JwsTenantContextVerifier({_KID: _SECRET}, now=lambda: just_after)
    ctx = verifier.verify(token)
    assert ctx.tenant_id == "tenant_abc"


@pytest.mark.unit
def test_wrong_typ_rejected() -> None:
    """A header typ other than tenantctx+jws is rejected (even with a valid HMAC)."""
    token = _sign_with_header(
        _CLAIMS, {"alg": "HS256", "kid": _KID, "typ": "JWT"}, _SECRET
    )

    with pytest.raises(InvalidTenantContext):
        _verifier().verify(token)


@pytest.mark.unit
def test_wrong_alg_rejected() -> None:
    """A header alg other than HS256 is rejected (alg pinned — no downgrade)."""
    token = _sign_with_header(
        _CLAIMS, {"alg": "none", "kid": _KID, "typ": "tenantctx+jws"}, _SECRET
    )

    with pytest.raises(InvalidTenantContext):
        _verifier().verify(token)


@pytest.mark.unit
@pytest.mark.parametrize(
    "bad_token",
    ["", "not-a-token", "only.two", "a.b.c.d", "header.payload"],
)
def test_malformed_token_rejected(bad_token: str) -> None:
    """Structurally malformed tokens (not exactly 3 parts / garbage) are rejected."""
    with pytest.raises(InvalidTenantContext):
        _verifier().verify(bad_token)


@pytest.mark.unit
def test_empty_key_set_rejected() -> None:
    """Constructing a verifier with no keys fails closed (ValueError)."""
    with pytest.raises(ValueError, match="at least one signing key"):
        JwsTenantContextVerifier({})


@pytest.mark.unit
def test_rotation_overlap() -> None:
    """Both kids in the active set verify; a retired kid stops verifying."""
    token_a = sign(_CLAIMS, secret="secret-a", kid="kid-a")
    token_b = sign(_CLAIMS, secret="secret-b", kid="kid-b")

    both = JwsTenantContextVerifier(
        {"kid-a": "secret-a", "kid-b": "secret-b"}, now=lambda: _PINNED_NOW
    )
    assert both.verify(token_a).tenant_id == "tenant_abc"
    assert both.verify(token_b).tenant_id == "tenant_abc"

    # Retire kid-a: only kid-b remains valid.
    rotated = JwsTenantContextVerifier({"kid-b": "secret-b"}, now=lambda: _PINNED_NOW)
    assert rotated.verify(token_b).tenant_id == "tenant_abc"
    with pytest.raises(InvalidTenantContext):
        rotated.verify(token_a)


# ---------------------------------------------------------------------------
# CROSS-IMPL VECTOR — shared with Phase 2 C# JwsTenantContextSignerTests
# ---------------------------------------------------------------------------

# The secret (raw string; HMAC key = its UTF-8 bytes), kid, and exact token below
# are byte-identical to the fixture in apps/api's JwsTenantContextSignerTests.
# Verifying it here proves the C# signer and this Python verifier agree on the
# same canonical wire form. The token's window is fixed, so the clock is pinned
# between issued_at (00:00:00Z) and expires_at (00:05:00Z).
_VECTOR_SECRET = "spec15-cross-impl-vector-secret"
_VECTOR_KID = "vector-kid-1"
_VECTOR_TOKEN = (
    "eyJhbGciOiJIUzI1NiIsImtpZCI6InZlY3Rvci1raWQtMSIsInR5cCI6InRlbmFudGN0eCtqd3MifQ"
    ".eyJhbGxvd2VkX2FnZW50X3BvbGljeV9pZHMiOlsiYWdlbnQtcG9saWN5LWEiLCJhZ2VudC1wb2xpY3"
    "ktYiJdLCJhbGxvd2VkX3Rvb2xfcG9saWN5X2lkcyI6WyJ0b29sLXBvbGljeS1cdTAwZmMiXSwiY2hhb"
    "m5lbCI6InZvaWNlIiwiY2hhbm5lbF9lbmRwb2ludF9pZCI6ImRpZDorMTU1NTAwMDExMTEiLCJjb25m"
    "aWd1cmF0aW9uX3ZlcnNpb24iOiJjZmctdjEiLCJjb3JyZWxhdGlvbl9pZCI6ImNvcnJfMDAwMDAwMDA"
    "wMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDEiLCJleHBpcmVzX2F0IjoiMjAyNi0wNy0yNVQwMDowNTowMF"
    "oiLCJpc3N1ZWRfYXQiOiIyMDI2LTA3LTI1VDAwOjAwOjAwWiIsInNlc3Npb25faWQiOiJzZXNzXzAwM"
    "DAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAxIiwidGVuYW50X2lkIjoidGVuYW50LWFiYyJ9"
    ".-6d8E_84lKWbIUdh8zCrMrqx76DAPX9UNW6axXhKT0M"
)
_VECTOR_NOW = datetime(2026, 7, 25, 0, 2, 0, tzinfo=UTC)


@pytest.mark.unit
def test_cross_impl_vector_verifies() -> None:
    """The exact Phase 2 C# token verifies here to the expected claim set."""
    verifier = JwsTenantContextVerifier(
        {_VECTOR_KID: _VECTOR_SECRET}, now=lambda: _VECTOR_NOW
    )

    ctx = verifier.verify(_VECTOR_TOKEN)

    assert ctx.tenant_id == "tenant-abc"
    assert ctx.channel is TenantChannel.voice
    assert ctx.channel_endpoint_id == "did:+15550001111"
    assert ctx.configuration_version == "cfg-v1"
    assert ctx.correlation_id == "corr_00000000000000000000000000000001"
    assert ctx.session_id == "sess_00000000000000000000000000000001"
    assert ctx.allowed_agent_policy_ids == ["agent-policy-a", "agent-policy-b"]
    # U+00FC (ü) survives the ensure_ascii ü round-trip.
    assert ctx.allowed_tool_policy_ids == ["tool-policy-ü"]
    assert ctx.issued_at == datetime(2026, 7, 25, 0, 0, 0, tzinfo=UTC)
    assert ctx.expires_at == datetime(2026, 7, 25, 0, 5, 0, tzinfo=UTC)


@pytest.mark.unit
def test_failure_message_never_leaks_secret() -> None:
    """Rejected-token messages contain neither the secret nor the token (Inv-12)."""
    # Tamper the vector token's payload so verification fails under the real secret.
    header_b64, payload_b64, sig_b64 = _VECTOR_TOKEN.split(".")
    tampered = f"{header_b64}.{payload_b64[:-2]}AA.{sig_b64}"
    verifier = JwsTenantContextVerifier(
        {_VECTOR_KID: _VECTOR_SECRET}, now=lambda: _VECTOR_NOW
    )

    with pytest.raises(InvalidTenantContext) as excinfo:
        verifier.verify(tampered)

    message = str(excinfo.value)
    assert _VECTOR_SECRET not in message
    assert _VECTOR_TOKEN not in message


# ---------------------------------------------------------------------------
# Config loader (keys_from_config)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_keys_from_config_reads_json_map(monkeypatch) -> None:
    """A JSON {kid: secret} map is parsed into the active-key set."""
    monkeypatch.setenv(
        SIGNING_KEYS_ENV_VAR, json.dumps({_KID: _SECRET, "kid-2": "secret-2"})
    )
    assert keys_from_config() == {_KID: _SECRET, "kid-2": "secret-2"}


@pytest.mark.unit
def test_keys_from_config_missing_fails_closed(monkeypatch) -> None:
    """An unset env var raises (fail closed) rather than returning an empty set."""
    monkeypatch.delenv(SIGNING_KEYS_ENV_VAR, raising=False)
    with pytest.raises(RuntimeError):
        keys_from_config()


@pytest.mark.unit
@pytest.mark.parametrize("raw", ["not json", "[]", "{}", '{"kid": ""}', '{"kid": 123}'])
def test_keys_from_config_invalid_fails_closed(monkeypatch, raw: str) -> None:
    """Non-object / empty / non-string-value configs all fail closed."""
    monkeypatch.setenv(SIGNING_KEYS_ENV_VAR, raw)
    with pytest.raises(RuntimeError):
        keys_from_config()


# ---------------------------------------------------------------------------
# Startup registration (bootstrap_verifier)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_bootstrap_registers_production_verifier(monkeypatch) -> None:
    """After bootstrap, get_verifier() no longer raises and returns the prod verifier."""
    monkeypatch.setenv(SIGNING_KEYS_ENV_VAR, json.dumps({_KID: _SECRET}))
    clear_verifier()

    bootstrap_verifier()

    verifier = get_verifier()  # must NOT raise (registry now wired)
    assert isinstance(verifier, JwsTenantContextVerifier)


@pytest.mark.unit
def test_bootstrap_without_config_leaves_registry_unset(monkeypatch) -> None:
    """With no env var, bootstrap registers nothing → get_verifier() still fails closed."""
    monkeypatch.delenv(SIGNING_KEYS_ENV_VAR, raising=False)
    clear_verifier()

    bootstrap_verifier()

    with pytest.raises(RuntimeError):
        get_verifier()


@pytest.mark.unit
def test_bootstrap_is_idempotent(monkeypatch) -> None:
    """Re-running bootstrap does not double-register / replace the existing verifier."""
    monkeypatch.setenv(SIGNING_KEYS_ENV_VAR, json.dumps({_KID: _SECRET}))
    clear_verifier()

    bootstrap_verifier()
    first = get_verifier()
    bootstrap_verifier()
    second = get_verifier()

    assert first is second


# ---------------------------------------------------------------------------
# resolve_tenant driven by the PRODUCTION verifier (not the stub)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_resolve_tenant_with_production_verifier() -> None:
    """resolve_tenant verifies a production-form token and populates its return fields."""
    register_verifier(_verifier())
    token = sign(_CLAIMS, secret=_SECRET, kid=_KID)
    state = AgentState(signed_tenant_context=token)

    result = await resolve_tenant(state)

    assert result["tenant_id"] == "tenant_abc"
    assert result["tenant_policy_version"] == "v2"
    assert result["correlation_id"] == "corr_xyz"
    assert result["channel_endpoint_id"] == "ep_001"
    assert result["session_id"] == "sess_001"
    assert result["allowed_agent_policy_ids"] == ["agent_policy_a"]
    assert result["allowed_tool_policy_ids"] == ["tool_policy_a", "tool_policy_b"]
    assert result["tenant_context_verified"] is True


@pytest.mark.unit
async def test_resolve_tenant_rejects_forged_token_with_production_verifier() -> None:
    """A token signed under the wrong secret is rejected by the graph gate (Inv-13)."""
    register_verifier(_verifier())
    forged = sign(dict(_CLAIMS, tenant_id="evil"), secret="wrong-secret", kid=_KID)
    state = AgentState(signed_tenant_context=forged)

    with pytest.raises(InvalidTenantContext):
        await resolve_tenant(state)
