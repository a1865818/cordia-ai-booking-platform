"""FROZEN SPEC-15 CONTRACT FAKE — replace when spec 15 lands.

This module is a test-only stub for the TenantContext verifier port.  It uses
HMAC-SHA256 over a canonical JSON serialisation of the claim set to produce and
verify signed tokens.

DO NOT import this module from production code.  It must only be referenced from
test fixtures (register_verifier / clear_verifier calls).

When spec 15 is implemented, replace ``StubTenantContextVerifier`` with the
real verifier and remove this module.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
from typing import Any

from cordia_agent.safety.tenant_context import InvalidTenantContext, TenantContext


# Default HMAC secret for the stub — tests may override via the `secret` kwarg.
_DEFAULT_SECRET = "stub-secret-do-not-use-in-production"


def _canonical_json(claims: dict[str, Any]) -> bytes:
    """Produce a deterministic, compact JSON encoding of *claims*."""
    return json.dumps(
        claims, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    ).encode()


def sign(
    claims: dict[str, Any] | TenantContext,
    *,
    secret: str = _DEFAULT_SECRET,
) -> str:
    """Sign *claims* with HMAC-SHA256 and return a base64url token.

    Token format: ``<base64url(payload)>.<base64url(signature)>``

    Args:
        claims: A dict of TenantContext fields, or a ``TenantContext`` instance.
        secret: HMAC secret; defaults to the stub default.

    Returns:
        A signed token string suitable for passing as ``signed_tenant_context``.
    """
    if isinstance(claims, TenantContext):
        claims_dict: dict[str, Any] = claims.model_dump(mode="json")
    else:
        claims_dict = dict(claims)

    payload_bytes = _canonical_json(claims_dict)
    payload_b64 = base64.urlsafe_b64encode(payload_bytes).rstrip(b"=").decode()

    sig_bytes = hmac.new(secret.encode(), payload_bytes, hashlib.sha256).digest()
    sig_b64 = base64.urlsafe_b64encode(sig_bytes).rstrip(b"=").decode()

    return f"{payload_b64}.{sig_b64}"


class StubTenantContextVerifier:
    """FROZEN SPEC-15 CONTRACT FAKE — test-only HMAC verifier.

    Implements the ``TenantContextVerifier`` port using HMAC-SHA256.
    Register this in test fixtures; clear in teardown.
    """

    def __init__(self, secret: str = _DEFAULT_SECRET) -> None:
        self._secret = secret

    def verify(self, signed_token: str) -> TenantContext:
        """Verify *signed_token* and return the decoded ``TenantContext``.

        Raises:
            InvalidTenantContext: if the token is malformed, the signature is
                wrong, or the payload cannot be parsed as a ``TenantContext``.
        """
        if not signed_token or "." not in signed_token:
            raise InvalidTenantContext("Malformed token: missing payload or signature")

        # Split on the last dot so payload may contain dots in field values.
        last_dot = signed_token.rfind(".")
        payload_b64 = signed_token[:last_dot]
        sig_b64 = signed_token[last_dot + 1 :]

        try:
            # Re-pad base64url
            padding = "=" * (-len(payload_b64) % 4)
            payload_bytes = base64.urlsafe_b64decode(payload_b64 + padding)
        except Exception as exc:
            raise InvalidTenantContext(f"Malformed token payload: {exc}") from exc

        # Recompute HMAC and compare in constant time.
        expected_sig_bytes = hmac.new(
            self._secret.encode(), payload_bytes, hashlib.sha256
        ).digest()
        expected_b64 = (
            base64.urlsafe_b64encode(expected_sig_bytes).rstrip(b"=").decode()
        )

        if not hmac.compare_digest(sig_b64, expected_b64):
            raise InvalidTenantContext(
                "Token signature verification failed (forged or tampered)"
            )

        try:
            claims_dict: dict[str, Any] = json.loads(payload_bytes)
        except json.JSONDecodeError as exc:
            raise InvalidTenantContext(
                f"Token payload is not valid JSON: {exc}"
            ) from exc

        try:
            return TenantContext.model_validate(claims_dict)
        except Exception as exc:
            raise InvalidTenantContext(
                f"Token payload fails TenantContext validation: {exc}"
            ) from exc
