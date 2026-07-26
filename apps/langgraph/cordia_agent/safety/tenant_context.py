"""Tenant-context port: frozen spec-15 TenantContext claim set + verifier registry.

Production code calls ``verify_signed_context(signed_token)`` to obtain a
verified ``TenantContext``.  The concrete verifier (spec-15 implementation)
is plugged in at startup via ``register_verifier()``.  Tests register a stub
(in ``tests/_stubs/``) and call ``clear_verifier()`` in teardown.

Invariant 13: tenant identity is derived SOLELY from the verified context —
no caller may supply a raw tenant_id that bypasses signature verification.
"""

from __future__ import annotations

from enum import Enum
from typing import Protocol, runtime_checkable

from pydantic import AwareDatetime, BaseModel, Field


class TenantChannel(str, Enum):
    """Origin channel a signed ``TenantContext`` was minted for (spec-15 §5.1).

    Distinct from :class:`cordia_agent.state.Channel` (which carries an ``sms``
    member for booking-draft provenance): the tenant-context wire contract uses
    ``messaging`` as the canonical two-way-text channel name so it byte-matches
    the C# ``TenantChannel`` enum generated from this same source of truth.
    """

    voice = "voice"
    messaging = "messaging"
    web = "web"
    email = "email"


class TenantContext(BaseModel):
    """Frozen spec-15 §5.1 TenantContext claim set.

    All fields are required; extra fields are rejected.  The model is frozen so
    that callers cannot mutate a verified context after it has been issued.
    """

    model_config = {"extra": "forbid", "frozen": True}

    tenant_id: str = Field(description="Stable tenant identifier (Inv-13 scope).")
    channel: TenantChannel = Field(
        description="Origin channel the request arrived on (spec-15 §5.1)."
    )
    channel_endpoint_id: str = Field(
        description="Channel-specific endpoint identifier (e.g. phone number, widget ID)."
    )
    configuration_version: str = Field(
        description="Tenant configuration/policy version active at session start."
    )
    correlation_id: str = Field(
        description="Correlation ID propagated from the channel gateway for distributed tracing."
    )
    session_id: str = Field(
        description=(
            "Conversation session identifier allocated by the gateway at ingress "
            "(spec-15 §5.1); scopes the session row and downstream audit events."
        )
    )
    allowed_agent_policy_ids: list[str] = Field(
        description=(
            "Ordered list of agent-policy IDs in force for this session "
            "(spec-15 §5.1; split from the former single allowed_policy_ids)."
        )
    )
    allowed_tool_policy_ids: list[str] = Field(
        description=(
            "Ordered list of tool-policy IDs authorising tool invocation for this "
            "session (spec-15 §5.1)."
        )
    )
    issued_at: AwareDatetime = Field(
        description="Timezone-aware instant the context was minted (spec-15 §5.1)."
    )
    expires_at: AwareDatetime = Field(
        description=(
            "Timezone-aware instant the context expires; verifiers reject a token "
            "presented after this instant (spec-15 §5.1)."
        )
    )


class InvalidTenantContext(Exception):
    """Raised when a signed tenant-context token cannot be verified.

    Raised by the verifier on a missing token, a bad signature, or a structurally
    invalid payload.  ``resolve_tenant`` propagates this exception to halt graph
    execution before any data-access node runs (§8.3 gate).
    """


@runtime_checkable
class TenantContextVerifier(Protocol):
    """Port: abstracts the spec-15 signing scheme from production nodes.

    Production registers the real verifier (spec-15 implementation).
    Tests register a HMAC-SHA256 stub (``tests/_stubs/tenant_context_stub.py``).
    """

    def verify(self, signed_token: str) -> TenantContext:
        """Verify *signed_token* and return the decoded ``TenantContext``.

        Raises:
            InvalidTenantContext: if the token is missing, structurally invalid,
                or the signature does not match.
        """
        ...


# ---------------------------------------------------------------------------
# Module-level verifier registry (pluggable port)
# ---------------------------------------------------------------------------

_verifier: TenantContextVerifier | None = None


def register_verifier(v: TenantContextVerifier) -> None:
    """Register the concrete verifier implementation for this process.

    Must be called once at startup (or in a test fixture) before any call to
    ``verify_signed_context``.
    """
    global _verifier
    _verifier = v


def get_verifier() -> TenantContextVerifier:
    """Return the registered verifier, or raise if none has been registered.

    Raises:
        RuntimeError: if ``register_verifier`` has not been called yet.
    """
    if _verifier is None:
        raise RuntimeError("no TenantContext verifier registered (spec 15 not wired)")
    return _verifier


def clear_verifier() -> None:
    """Remove the registered verifier — call in test teardown to isolate tests."""
    global _verifier
    _verifier = None


def verify_signed_context(signed_token: str) -> TenantContext:
    """Delegate to the registered verifier and return the verified ``TenantContext``.

    Args:
        signed_token: The opaque signed token produced by the channel gateway.

    Returns:
        A verified, immutable ``TenantContext``.

    Raises:
        InvalidTenantContext: if the token is invalid or the verifier rejects it.
        RuntimeError: if no verifier has been registered.
    """
    return get_verifier().verify(signed_token)
