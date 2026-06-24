"""Tenant-context port: frozen spec-15 TenantContext claim set + verifier registry.

Production code calls ``verify_signed_context(signed_token)`` to obtain a
verified ``TenantContext``.  The concrete verifier (spec-15 implementation)
is plugged in at startup via ``register_verifier()``.  Tests register a stub
(in ``tests/_stubs/``) and call ``clear_verifier()`` in teardown.

Invariant 13: tenant identity is derived SOLELY from the verified context —
no caller may supply a raw tenant_id that bypasses signature verification.
"""

from __future__ import annotations

from typing import Protocol, runtime_checkable

from pydantic import BaseModel, Field

from cordia_agent.state import Channel


class TenantContext(BaseModel):
    """Frozen spec-15 TenantContext claim set (line 55 of the foreground-agent spec).

    All fields are required; extra fields are rejected.  The model is frozen so
    that callers cannot mutate a verified context after it has been issued.
    """

    model_config = {"extra": "forbid", "frozen": True}

    tenant_id: str = Field(description="Stable tenant identifier (Inv-13 scope).")
    channel: Channel = Field(description="Origin channel the request arrived on.")
    channel_endpoint_id: str = Field(
        description="Channel-specific endpoint identifier (e.g. phone number, widget ID)."
    )
    configuration_version: str = Field(
        description="Tenant configuration/policy version active at session start."
    )
    correlation_id: str = Field(
        description="Correlation ID propagated from the channel gateway for distributed tracing."
    )
    allowed_policy_ids: list[str] = Field(
        description="Ordered list of policy IDs the tenant is allowed to invoke."
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
